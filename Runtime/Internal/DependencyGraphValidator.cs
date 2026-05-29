using System;
using System.Collections.Generic;
using System.Reflection;
using Didionysymus.Lattice.Runtime.Exceptions;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Validates a registry's dependency graph at registration time.
    /// Catches cycles and captive dependencies before the first resolve so misconfiguration
    /// surfaces at <see cref="Container.Build"/> or <see cref="IObjectResolver.CreateChildScope"/>
    /// rather than at first <see cref="IObjectResolver.Resolve{T}"/> call.
    /// </summary>
    /// <remarks>
    /// Walks two graphs:
    /// <list type="bullet">
    ///     <item>
    ///         <description>
    ///             <see cref="Registry.ClosedRegistry"/>: fully closed registrations, including any preserved closed-generics
    ///             already promoted by <c>PrebuildPreservedClosedGenerics</c>
    ///         </description>
    ///     </item>
    ///     <item>
    ///         <description>
    ///             <see cref="Registry.OpenRegistry"/>: open-generic registrations, walked against the open implementation's constructor
    ///             parameters. Open parameters resolve to other <see cref="OpenGenericEntry"/> entries; closed parameters resolve
    ///             through the existing closed walk.
    ///         </description>
    ///     </item>
    /// </list>
    /// </remarks>
    internal static class DependencyGraphValidator
    {
        /// <summary>
        /// Validates the given registry against its parent chain.
        /// </summary>
        /// <param name="registry">The registry to validate (registrations local to a single scope).</param>
        /// <param name="parentChain">
        /// Parent registries from immediate parent up to root, used by the captive-dependency check to
        /// reason about scope boundaries.
        /// </param>
        public static void Validate(Registry registry, IEnumerable<Registry> parentChain)
        {
            List<Registry> allRegistries = new List<Registry> { registry };
            foreach (Registry parent in parentChain)
            {
                allRegistries.Add(parent);
            }

            // Walk closed registrations
            for (int i = 0; i < registry.RegistrationOrder.Count; i++)
            {
                RegistrationEntry entry = registry.RegistrationOrder[i];

                // Pre-built instances bypass construction; no constructor params to walk
                if (entry.IsPreBuiltInstance) continue;

                // Each top-level walk gets its own visited/path bookkeeping. The outer lifetime
                // is captured here and stays constant through the descent so that transitive
                // captive checks (e.g., Singleton -> Transient -> Scoped) fire.
                HashSet<Type> visited = new HashSet<Type>();
                List<Type> path = new List<Type>();

                Walk(
                    entry,
                    allRegistries,
                    visited,
                    path,
                    outerLifetime: entry.Lifetime,
                    outerType: entry.ImplType
                );
            }

            // Walk open-generic registrations. Each open entry's implementation type is a generic
            // type definition; we walk its constructor parameters and resolve open parametesr through OpenRegistry,
            // closed parameters through ClosedRegistry. Catches captive violations in the closed-on-demand promotion path
            // that runs at runtime (after this validator has finished).
            foreach (OpenGenericEntry openEntry in registry.OpenRegistry.Values)
            {
                HashSet<Type> visited = new HashSet<Type>();
                List<Type> path = new List<Type>();

                WalkOpen(
                    openEntry,
                    allRegistries,
                    visited,
                    path,
                    outerLifetime: openEntry.Lifetime,
                    outerType: openEntry.OpenImplType
                );
            }
        }

        /// <summary>
        /// Traverses the dependency graph starting from the provided registration entry to validate
        /// the shape of the graph and detect issues like cyclic dependencies or lifetime mismatches.
        /// </summary>
        /// <param name="entry">The registration entry representing the starting point of the graph traversal.</param>
        /// <param name="registries">A list of registries containing available registrations used for dependency resolution.</param>
        /// <param name="visited">
        /// A set of visited types to prevent revisiting during the traversal, used to detect cycles
        /// in the dependency graph.
        /// </param>
        /// <param name="path">
        /// A list representing the current path of types being traversed, used to provide context
        /// for error reporting when issues are found.
        /// </param>
        /// <param name="outerLifetime">
        /// The lifetime of the outer registration that initiated the current traversal, used to check
        /// for lifetime mismatches in the dependency chain.
        /// </param>
        /// <param name="outerType">
        /// The type of the outer registration that initiated the current traversal, used for contextual
        /// error reporting.
        /// </param>
        /// <exception cref="CyclicDependencyException">
        /// Thrown when a cyclic dependency is detected during the traversal.
        /// </exception>
        private static void Walk(
            RegistrationEntry entry,
            List<Registry> registries,
            HashSet<Type> visited,
            List<Type> path,
            Lifetime outerLifetime,
            Type outerType
        )
        {
            if (entry.IsPreBuiltInstance) return;

            if (!visited.Add(entry.ImplType))
            {
                // Already visited this implementation. If it's also on the current path, that's a cycle;
                // otherwise it's just a diamond (already validated through another route).
                if (path.Contains(entry.ImplType))
                {
                    // Cut the cycle: the prefix of 'path' up to entry.ImplType
                    // is the body of the cycle; append entry.ImplType again to close
                    // the loop visually
                    int startIndex = path.IndexOf(entry.ImplType);
                    List<Type> cycle = path.GetRange(startIndex, path.Count - startIndex);
                    cycle.Add(entry.ImplType);
                    throw new CyclicDependencyException(cycle);
                }

                return;
            }

            path.Add(entry.ImplType);

            // Resolve the constructor we'd use at runtime. If selection itself fails (e.g., no public constructor),
            // defer that error to runtime; validator's job is graph shape only.
            ConstructorInfo constructor;
            try
            {
                constructor = ConstructorSelector.SelectConstructor(entry.ImplType);
            }
            catch (DependencyResolutionException)
            {
                path.RemoveAt(path.Count - 1);
                return;
            }

            ParameterInfo[] parameters = constructor.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo parameter = parameters[i];
                Type parameterType = parameter.ParameterType;

                // Auto-multibinding parameters (IReadOnlyList<T>, T[], etc.): unwrap to element type.
                // Note: only the last-registered implementation is validated here
                if (IsAutoMultiBound(parameterType, out Type elementType))
                    parameterType = elementType;

                // Skip types the validator can't (or shouldn't) reason about:
                // - value types and string: never DI-resolved
                // - IObjectResolver: special pseudo-dependency the container injects directly
                // - Func<T>: factory pattern; registration shape can't tell us about lifetime captivity
                if (parameterType.IsValueType || parameterType == typeof(string)) continue;
                if (parameterType == typeof(IObjectResolver)) continue;
                if (parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(Func<>)) continue;

                RegistrationEntry inner = FindRegistration(parameterType, registries);
                if (inner == null)
                {
                    // Optional [Inject] params with no registration are fine
                    bool optional = parameter.IsDefined(typeof(InjectAttribute), inherit: false)
                                    && parameter.GetCustomAttribute<InjectAttribute>().Optional;

                    if (optional) continue;

                    // Tolerate missing registration here; runtime will raise RegistrationNotFoundException
                    // if the type actually gets requested. Open-generic parameters are handled in WalkOpen.
                    continue;
                }

                CheckCaptive(
                    outerType,
                    outerLifetime,
                    inner,
                    path
                );

                Walk(
                    inner,
                    registries,
                    visited,
                    path,
                    outerLifetime,
                    outerType
                );
            }

            path.RemoveAt(path.Count - 1);
        }

        /// <summary>
        /// Traverses the open-generic dependency graph starting from the provided open-generic entry.
        /// Closed parameters fall through to <see cref="Walk"/>; open parameters recurse into <see cref="WalkOpen"/>;
        /// bare type-parameter references (e.g., <c>T</c>) are skipped.
        /// </summary>
        /// <param name="openEntry">The open generic entry representing the starting point of the traversal.</param>
        /// <param name="registries">The list of registries containing all available service registrations.</param>
        /// <param name="visited">The set of types already visited during the traversal to prevent infinite loops.</param>
        /// <param name="path">The current path of types being traversed, used to detect circular dependencies.</param>
        /// <param name="outerLifetime">The lifetime of the outer dependency used to validate scope boundaries.</param>
        /// <param name="outerType">The outer type being resolved, used to ensure appropriate type relationships.</param>
        private static void WalkOpen(
            OpenGenericEntry openEntry,
            List<Registry> registries,
            HashSet<Type> visited,
            List<Type> path,
            Lifetime outerLifetime,
            Type outerType
        )
        {
            if (!visited.Add(openEntry.OpenImplType))
            {
                if (path.Contains(openEntry.OpenImplType))
                {
                    int startIndex = path.IndexOf(openEntry.OpenImplType);
                    List<Type> cycle = path.GetRange(startIndex, path.Count - startIndex);
                    cycle.Add(openEntry.OpenImplType);
                    throw new CyclicDependencyException(cycle);
                }

                return;
            }

            path.Add(openEntry.OpenImplType);

            ConstructorInfo constructor;
            try
            {
                constructor = ConstructorSelector.SelectConstructor(openEntry.OpenImplType);
            }
            catch (DependencyResolutionException)
            {
                path.RemoveAt(path.Count - 1);
                return;
            }

            ParameterInfo[] parameters = constructor.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo parameter = parameters[i];
                Type parameterType = parameter.ParameterType;

                if (IsAutoMultiBound(parameterType, out Type elementType)) parameterType = elementType;
                if (parameterType.IsValueType || parameterType == typeof(string)) continue;
                if (parameterType == typeof(IObjectResolver)) continue;
                if (parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(Func<>)) continue;

                // Bare generic parameter (e.g., T): cannot reason about it without a closing arg
                if (parameterType.IsGenericParameter) continue;

                // Open generic parameter (e.g., IRepository<T> where T is the outer's type-parameter): resolve via OpenRegistry.
                // ContainsGenericParameters distinguishes this from a fully closed type.
                if (parameterType.ContainsGenericParameters)
                {
                    if (!parameterType.IsGenericType) continue;

                    Type openParameterDefinition = parameterType.GetGenericTypeDefinition();
                    OpenGenericEntry innerOpen = FindOpenRegistration(openParameterDefinition, registries);
                    if (innerOpen == null) continue;

                    CheckCaptiveOpen(
                        outerType,
                        outerLifetime,
                        innerOpen,
                        path
                    );

                    WalkOpen(
                        innerOpen,
                        registries,
                        visited,
                        path,
                        outerLifetime,
                        outerType
                    );

                    continue;
                }

                // Fully-closed parameter type: resolve through the closed graph
                RegistrationEntry inner = FindRegistration(parameterType, registries);
                if (inner == null)
                {
                    bool optional = parameter.IsDefined(typeof(InjectAttribute), inherit: false) &&
                                    parameter.GetCustomAttribute<InjectAttribute>().Optional;
                    if (optional) continue;

                    continue;
                }

                CheckCaptive(
                    outerType,
                    outerLifetime,
                    inner,
                    path
                );

                Walk(
                    inner,
                    registries,
                    visited,
                    path,
                    outerLifetime,
                    outerType
                );
            }

            path.RemoveAt(path.Count - 1);
        }

        /// <summary>
        /// Determines whether the provided parameter type is eligible for auto-multibinding and extracts its element type if applicable.
        /// Auto-multibinding applies to collection types such as arrays, lists, and enumerable sequences.
        /// </summary>
        /// <param name="parameterType">The type of the parameter to check for auto-multibinding eligibility.</param>
        /// <param name="elementType">
        /// When this method returns <c>true</c>, this parameter contains the element type for the collection.
        /// If <c>false</c>, this value will be <c>null</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> if the provided parameter type is recognized as a collection type eligible for auto-multibinding;
        /// otherwise, <c>false</c>.
        /// </returns>
        private static bool IsAutoMultiBound(Type parameterType, out Type elementType)
        {
            if (parameterType.IsArray)
            {
                elementType = parameterType.GetElementType();
                return true;
            }

            if (parameterType.IsGenericType)
            {
                Type definition = parameterType.GetGenericTypeDefinition();
                if (definition == typeof(IReadOnlyList<>)
                    || definition == typeof(IEnumerable<>)
                    || definition == typeof(IList<>)
                    || definition == typeof(List<>)
                   )
                {
                    elementType = parameterType.GetGenericArguments()[0];
                    return true;
                }
            }

            elementType = null;
            return false;
        }

        /// <summary>
        /// Searches for a registration entry matching the specified service type within the provided list of registries.
        /// </summary>
        /// <param name="serviceType">The service type to locate within the registries.</param>
        /// <param name="registries">The list of registries to search for the service registration.</param>
        /// <returns>
        /// The most recent registration entry for the specified service type, or null if no match is found.
        /// </returns>
        private static RegistrationEntry FindRegistration(Type serviceType, List<Registry> registries)
        {
            for (int i = 0; i < registries.Count; i++)
            {
                Registry r = registries[i];
                if (r.ClosedRegistry.TryGetValue(serviceType, out List<RegistrationEntry> list) && list.Count > 0)
                {
                    return list[^1];
                }
            }

            return null;
        }

        /// <summary>
        /// Finds the open generic entry registration for a given service definition
        /// within the provided list of registries.
        /// </summary>
        /// <param name="openServiceDefinition">The generic type definition of the service to locate within the registries.</param>
        /// <param name="registries">The list of registries to search for the open generic entry registration.</param>
        /// <returns>The open generic entry corresponding to the specified service definition if found; otherwise, null.</returns>
        private static OpenGenericEntry FindOpenRegistration(Type openServiceDefinition, List<Registry> registries)
        {
            for (int i = 0; i < registries.Count; i++)
            {
                Registry r = registries[i];

                if (!r.OpenRegistry.TryGetValue(openServiceDefinition, out OpenGenericEntry entry))
                    continue;

                return entry;
            }

            return null;
        }

        /// <summary>
        /// Checks for captive dependency violations in the dependency graph by
        /// verifying if a Scoped dependency is accessed from a Singleton context.
        /// </summary>
        /// <param name="outerType">The type requesting a dependency injection, which defines the outer context.</param>
        /// <param name="outerLifetime">The lifetime of the outer type, determining its scope.</param>
        /// <param name="inner">The registration entry representing the dependent type and its associated lifetime.</param>
        /// <param name="path">The chain of types representing the resolution path in the dependency graph up to the current point.</param>
        /// <exception cref="CaptiveDependencyException">
        /// Thrown when a captive dependency violation is detected, indicating a Singleton type
        /// depends on a Scoped type, leading to potential lifecycle conflicts.
        /// </exception>
        private static void CheckCaptive(
            Type outerType,
            Lifetime outerLifetime,
            RegistrationEntry inner,
            List<Type> path
        )
        {
            bool violation = outerLifetime == Lifetime.Singleton && inner.Lifetime == Lifetime.Scoped;
            if (!violation) return;

            // Build the chain shown in the exception: current path and the offending inner type
            List<Type> chain = new List<Type>(path);
            chain.Add(inner.ImplType);

            throw new CaptiveDependencyException(
                outerType,
                outerLifetime,
                inner.ImplType,
                inner.Lifetime,
                chain
            );
        }

        /// <summary>
        /// Checks for captive dependency scenarios in open generic registrations, where a scoped dependency
        /// is captured by a singleton component.
        /// </summary>
        /// <param name="outerType">The type of the outer component to which the open generic dependency is applied.</param>
        /// <param name="outerLifetime">The lifetime of the outer component.</param>
        /// <param name="inner">The open generic entry representing the dependency being validated.</param>
        /// <param name="path">The current path in the dependency chain that is used to track relationships between components.</param>
        /// <exception cref="CaptiveDependencyException">Thrown when a scoped dependency is incorrectly captured by a singleton component.</exception>
        private static void CheckCaptiveOpen(
            Type outerType,
            Lifetime outerLifetime,
            OpenGenericEntry inner,
            List<Type> path
        )
        {
            bool violation = outerLifetime == Lifetime.Singleton && inner.Lifetime == Lifetime.Scoped;
            if (!violation) return;

            List<Type> chain = new List<Type>(path);
            chain.Add(inner.OpenImplType);

            throw new CaptiveDependencyException(
                outerType,
                outerLifetime,
                inner.OpenImplType,
                inner.Lifetime,
                chain
            );
        }
    }
}