using System;
using System.Collections.Generic;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Internal data store for all registrations and decorator chains within a scope. 
    /// Keyed by service type to support multi-binding via ResolveAll.
    /// </summary>
    internal sealed class Registry
    {
        public Dictionary<Type, List<RegistrationEntry>> ClosedRegistry { get; } =
            new Dictionary<Type, List<RegistrationEntry>>();

        public Dictionary<Type, List<DecoratorEntry>> DecoratorChains { get; } =
            new Dictionary<Type, List<DecoratorEntry>>();

        public List<Type> PreservedClosedGenerics { get; } = new List<Type>();

        /// <summary>
        /// Flat list of every closed registration in <c>Add</c> order. Mirrors what
        /// <see cref="ClosedRegistry"/> already preserves within each per-service type bucket; what this list
        /// adds is cross-service-type registration order. Used by the diagnostics dump and by <see cref="DependencyGraphValidator"/>
        /// for deterministic walk order. Decorators (<see cref="DecoratorChains"/>) and open generics (<see cref="OpenRegistry"/>) are not
        /// added here; they are surfaced separately in dump output and validation.
        /// </summary>
        public List<RegistrationEntry> RegistrationOrder { get; } = new List<RegistrationEntry>();

        /// <summary>
        /// Open-generic registry. Keyed by the open service-type definition (e.g., <c>typeof(IRepository&lt;&gt;)</c>);
        /// values describe how to manufacture a closed <see cref="RegistrationEntry"/> on demand.
        /// </summary>
        public Dictionary<Type, OpenGenericEntry> OpenRegistry { get; } = new Dictionary<Type, OpenGenericEntry>();

        /// <summary>
        /// Adds a registration entry for the specified service type, creating the service-type bucket if it does not exist.
        /// </summary>
        /// <param name="entry">The registration entry to add, specifying the service type, implementation type, lifetime, and activation strategy.</param>
        public void Add(RegistrationEntry entry)
        {
            if (!ClosedRegistry.TryGetValue(entry.ServiceType, out List<RegistrationEntry> list))
            {
                list = new List<RegistrationEntry>();
                ClosedRegistry[entry.ServiceType] = list;
            }

            list.Add(entry);
            RegistrationOrder.Add(entry);
        }

        /// <summary>
        /// Adds a decorator entry for the specified service type, creating the service-type bucket if needed.
        /// </summary>
        /// <param name="entry">The decorator entry to add, specifying the service type, decorator type, and lifetime.</param>
        public void AddDecorator(DecoratorEntry entry)
        {
            if (!DecoratorChains.TryGetValue(entry.ServiceType, out List<DecoratorEntry> list))
            {
                list = new List<DecoratorEntry>();
                DecoratorChains[entry.ServiceType] = list;
            }

            list.Add(entry);
        }

        /// <summary>
        /// Stores an open-generic registration in <see cref="OpenRegistry"/>.
        /// Any prior entry with the same open service type is overwritten.
        /// </summary>
        /// <param name="entry">The open-generic entry to add.</param>
        public void AddOpenGeneric(OpenGenericEntry entry)
        {
            OpenRegistry[entry.OpenServiceType] = entry;
        }

        /// <summary>
        /// Walks <see cref="PreservedClosedGenerics"/> and pre-builds a closed <see cref="RegistrationEntry"/> for every entry whose
        /// open service-type definition is found in <see cref="OpenRegistry"/> (this registry first, then <paramref name="parentChain"/> in order).
        /// Any preserved closed type that is already present in <see cref="ClosedRegistry"/> is skipped (an explicit <c>Register</c> wins).
        /// </summary>
        /// <remarks>
        /// Called by <see cref="Container.Build"/> (with <paramref name="parentChain"/> null) and by <c>Scope.CreateChildScope</c> (with the scope's
        /// parent chain). Pre-building amortizes the expression-tree compile cost for closed types declared via <c>PreserveClosedGenerics</c> so that the first runtime
        /// <see cref="IObjectResolver.Resolve{T}"/> of those types pays no compilation cost. Per our preservation strategy, every preserved closed
        /// type is also visible to the IL2CPP code-generator at compile time.
        /// </remarks>
        /// <param name="parentChain">Parent registries from immediate parent up to root. Null for root-scope pre-build (<see cref="Container.Build"/>).</param>
        public void PrebuildPreservedClosedGenerics(IEnumerable<Registry> parentChain = null)
        {
            // Materialize once. parentChain typically arrives as a yield-iterator (Scope.ParentRegistries), and
            // we'd otherwise re-walk the parent chain per preserved closed type
            List<Registry> parents = null;
            if (parentChain != null)
            {
                parents = new List<Registry>();
                foreach (Registry parent in parentChain)
                {
                    parents.Add(parent);
                }
            }

            ExpressionActivatorFactory factory = new ExpressionActivatorFactory();
            for (int i = 0; i < PreservedClosedGenerics.Count; i++)
            {
                Type closedType = PreservedClosedGenerics[i];

                // Skip if an explicit Register already covers this closed type
                if (ClosedRegistry.ContainsKey(closedType)) continue;
                if (!closedType.IsGenericType) continue;

                Type openDefinition = closedType.GetGenericTypeDefinition();

                // Resolve the open registration: this registry first, then walk parentChain in order
                if (!OpenRegistry.TryGetValue(openDefinition, out OpenGenericEntry openEntry) && parents != null)
                {
                    for (int j = 0; j < parents.Count; j++)
                    {
                        if (parents[j].OpenRegistry.TryGetValue(openDefinition, out openEntry)) break;
                    }
                }

                // No matching open registration anywhere in the chain. Tolerate silently; the runtime resolver will
                // throw RegistrationNotFoundException if this closed type is ever requested
                if (openEntry == null) continue;

                Type closedImpl = openEntry.OpenImplType.MakeGenericType(closedType.GetGenericArguments());
                RegistrationEntry entry = new RegistrationEntry(
                    serviceType: closedType,
                    implType: closedImpl,
                    lifetime: openEntry.Lifetime,
                    activator: factory.Build(closedImpl),
                    instance: null
                );
                Add(entry);
            }
        }
    }
}