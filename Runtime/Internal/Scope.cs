using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime.Exceptions;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// The runtime resolver and the concrete <see cref="IObjectResolver"/> backing every
    /// container. One <see cref="Scope"/> instance owns one <see cref="Registry"/> (its local registrations)
    /// plus a parent reference (null on the root). Resolution walks the parent chain on miss; child scopes can register
    /// additional services that shadow but do not replace parent registrations.
    /// </summary>
    internal sealed class Scope : IObjectResolver
    {
        private readonly Scope _parent;
        private readonly Registry _registry;

        // Per-scope cache of materialized Scope lifetime instances, keyed by entry identity.
        private readonly Dictionary<RegistrationEntry, object> _scopedCache =
            new Dictionary<RegistrationEntry, object>();

        // All IDisposables created within this scope, in creation order. Disposed in reverse on Dispose().
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        // Per-thread set of implementation types currently being resolved on this scope's call chain.
        // Catches cycles the registration-time validator missed (e.g., factory-introduced cycles)
        private readonly ThreadLocal<HashSet<Type>> _resolutionStack =
            new ThreadLocal<HashSet<Type>>(() => new HashSet<Type>());

        private readonly object _lock = new object();
        private bool _disposed;

        // Singleton cache lives only on the root scope. Child scopes route
        // Singleton lookups via Root.
        private readonly Dictionary<RegistrationEntry, object> _singletonCache;

        // Tracks which IInitializable instances have already had Initialize() called.
        // Ensures Initialize runs at-most-once per instance, even across repeated
        // RunInitializables calls
        private readonly HashSet<object> _initialized = new HashSet<object>();

        // Children registered against this scope; populated by their constructors and drained on Dispose (cascade). Walked
        // by DiagnosticsExtensions.DumpScopeTree to prin the scope tree root-down
        private readonly List<Scope> _children = new List<Scope>();

        // Process-static monotonic counter that mints a fresh ScopeID for every Scope instance. Scope IDs
        // vary between processes; they exist for diagnostics dump output, not for cross-process identity.
        private static int _nextScopeID;

        /// <summary>
        /// The root scope (walks up the parent chain). Returns this when this is the root.
        /// </summary>
        internal Scope Root => _parent?.Root ?? this;

        /// <summary>
        /// The parent scope, or null when this is the root.
        /// </summary>
        internal Scope Parent => _parent;

        /// <summary>
        /// The registrations local to this scope (does not include parent registrations).
        /// </summary>
        internal Registry Registry => _registry;

        /// <summary>
        /// Children registered against this scope. Walked by <see cref="DiagnosticsExtensions.DumpScopeTree"/>;
        /// drained on <see cref="Dispose"/> (cascade).
        /// </summary>
        internal IReadOnlyList<Scope> Children => _children;

        /// <summary>
        /// A process-unique identifier minted at construction. Stable for the lifetime of the process; rendered as <c>Scope#N</c>
        /// in diagnostics dump output.
        /// </summary>
        internal int ScopeID { get; } = Interlocked.Increment(ref _nextScopeID);

        public Scope(Registry registry, Scope parent)
        {
            _registry = registry;
            _parent = parent;

            // Only the root has a singleton cache; child scopes route Singleton
            // lookups via Root
            _singletonCache = parent == null
                ? new Dictionary<RegistrationEntry, object>()
                : null;

            if (parent == null) return;

            // Register with parent so the parent's Dispose can cascade.
            // Lock the parent's _lock to keep _children mutation thread-safe (parents
            // may be alive on another thread when a child is constructed).
            lock (parent._lock)
            {
                parent._children.Add(this);
            }
        }

        /// <summary>
        /// Resolves an instance of the specified type that is registered within the current dependency injection scope
        /// or any applicable parent scope.
        /// </summary>
        /// <typeparam name="T">The type of the service to resolve.</typeparam>
        /// <returns>An instance of the requested type.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when the current scope has been disposed before invocation.</exception>
        /// <exception cref="InvalidOperationException">Thrown when no registration is found for the specified type.</exception>
        public T Resolve<T>() => (T)Resolve(typeof(T));

        /// <summary>
        /// Resolves an instance of the specified type that is registered within the current scope
        /// or any applicable parent scope.
        /// </summary>
        /// <param name="type">The type of the service to be resolved.</param>
        /// <returns>An instance of the requested type.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the scope has been disposed prior to method invocation.</exception>
        /// <exception cref="InvalidOperationException">Thrown if no registration is found for the specified type.</exception>
        public object Resolve(Type type)
        {
            ThrowIfDisposed();
            return ResolveInternal(type, fromScope: this);
        }

        /// <summary>
        /// Resolves all instances of the specified type that are registered within the current scope
        /// and any applicable parent scopes.
        /// </summary>
        /// <typeparam name="T">The type of the service to be resolved.</typeparam>
        /// <returns>A read-only list of instances of the requested type.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the scope has been disposed prior to method invocation.</exception>
        public IReadOnlyList<T> ResolveAll<T>()
        {
            ThrowIfDisposed();
            List<(RegistrationEntry RegistrationEntry, Scope scope)> entries = CollectAllEntries(typeof(T));
            List<T> result = new List<T>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                (RegistrationEntry entry, Scope owningScope) = entries[i];
                result.Add((T)owningScope.MaterializeFromEntry(entry, this));
            }

            return result;
        }

        public IObjectResolver CreateChildScope(Action<IContainerBuilder> configure)
        {
            ThrowIfDisposed();
            ContainerBuilder childBuilder = new ContainerBuilder();
            configure.Invoke(childBuilder);

            // Promote PreserveClosedGenerics declarations into the child's closed registry before validation.
            // Pass the parent chain so child-scope preserved-close-generics can resolve against an open
            // registration declared on a parent
            childBuilder.Registry.PrebuildPreservedClosedGenerics(parentChain: ParentRegistriesIncludingSelf());

            // Validate the child's registrations against the full parent chain so captive-dependency
            // and cycle errors surface at scope creation, not at first resolve.
            DependencyGraphValidator.Validate(childBuilder.Registry, parentChain: ParentRegistries());

            return new Scope(childBuilder.Registry, this);
        }

        /// <summary>
        /// Phase 1 of the two-phase binding contract. Walks the cached <see cref="Internal.InjectionPlan"/> for <paramref name="instance"/>'s
        /// concrete type, resolving and assigning every <c>[Inject]</c>-annotated field and property, then invoking the at-most-one <c>[Inject]</c> method
        /// with each parameter resolved from this scope. Members marked <c>[Inject(Optional = true)]</c> tolerate a missing registration by receiving the type's
        /// default value rather than throwing <see cref="Exceptions.RegistrationNotFoundException"/>.
        /// </summary>
        /// <remarks>
        /// Phase 2 (<see cref="IInitializable.Initialize"/>) is run separately by <see cref="ResolverLifecycleExtensions.RunInitializables"/>; by contract,
        /// an instance's <c>Initialize</c> body may safely observe its own bound members and any peer instances in the same materialization wave.
        /// </remarks>
        /// <param name="instance">The target whose <c>[Inject]</c> members are to be bound. Must not be null.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="instance"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when this scope has already been disposed.</exception>
        public void Inject(object instance)
        {
            ThrowIfDisposed();
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            InjectInternal(instance);
        }

        /// <summary>
        /// Phase 2 binding worker. Separated from <see cref="Inject"/> so future internal callers
        /// (e.g., the Unity-side <c>InjectGameObject</c> walker after batch ThrowIfDisposed checks)
        /// can skip the public-API guards while still sharing the cache lookup and per-member assignment loop.
        /// </summary>
        /// <param name="instance">The object instance into which dependencies will be injected. Mst not be null.</param>
        /// <exception cref="ArgumentNullException">Thrown when the <paramref name="instance"/> parameter is null.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the current scope has been disposed and the operation is attempted.</exception>
        private void InjectInternal(object instance)
        {
            InjectionPlan plan = InjectionPlanCache.GetPlan(instance.GetType());

            for (int i = 0; i < plan.Members.Length; i++)
            {
                InjectableMember member = plan.Members[i];
                object value = ResolveMember(member);
                member.Setter.Invoke(instance, value);
            }

            if (plan.Method == null) return;

            ParameterInfo[] parameters = plan.Method.Parameters;
            object[] args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                args[i] = ResolveMethodParam(parameters[i]);
            }

            plan.Method.Invoker.Invoke(instance, args);
        }

        /// <summary>
        /// Resolves the value for a single <c>[Inject]</c>-annotated field or property. When the member is marked optional
        /// and no registration is found, returns <c>default(MemberType)</c> instead of propagating the lookup failure: <c>null</c>
        /// for reference types, the zeroed struct for value types.
        /// </summary>
        /// <param name="member">The member for which a value is being resolved, including its type and optional constraints.</param>
        /// <returns>
        /// The resolved value of the member if a registration is found; otherwise, the default value for the member's type
        /// if the member is optional and unregistered, or null for reference types.
        /// </returns>
        /// <exception cref="RegistrationNotFoundException">
        /// Thrown when no registration is found for the member's type
        /// and the member is not marked as optional.
        /// </exception>
        private object ResolveMember(InjectableMember member)
        {
            try
            {
                return Resolve(member.MemberType);
            }
            catch (RegistrationNotFoundException) when (member.Optional)
            {
                return member.MemberType.IsValueType
                    ? Activator.CreateInstance(member.MemberType)
                    : null;
            }
        }

        /// <summary>
        /// Resolves the value for a single <c>[Inject]</c> method parameter. Per-parameter optionality is
        /// honored via <see cref="InjectAttribute.Optional"/> on the <see cref="ParameterInfo"/>; missing
        /// <c>default(ParameterType</c>.
        /// </summary>
        /// <param name="parameter">The parameter metadata containing information about the type and custom attributes.</param>
        /// <returns>
        /// The resolved object for the specified parameter, or a default value/null if optional resolution is allowed
        /// and no registration is found.
        /// </returns>
        /// <exception cref="RegistrationNotFoundException">
        /// Thrown when the parameter type cannot be resolved
        /// and the parameter is not marked as optional.
        /// </exception>
        private object ResolveMethodParam(ParameterInfo parameter)
        {
            Type parameterType = parameter.ParameterType;
            InjectAttribute attribute = parameter.GetCustomAttribute<InjectAttribute>(inherit: false);
            bool optional = attribute is { Optional: true };

            try
            {
                return Resolve(parameterType);
            }
            catch (RegistrationNotFoundException) when (optional)
            {
                return parameterType.IsValueType
                    ? Activator.CreateInstance(parameterType)
                    : null;
            }
        }

        /// <summary>
        /// Resolves an instance of the specified type within the context of the provided scope.
        /// Attempts to locate a corresponding registration in the current registry and resolves it recursively,
        /// traversing parent scopes when necessary.
        /// </summary>
        /// <param name="type">The type of the service to resolve.</param>
        /// <param name="fromScope">The scope within which the resolution begins. Used to track the resolution chain and lifecycle.</param>
        /// <returns>An instance of the requested service if found.</returns>
        /// <exception cref="RegistrationNotFoundException">Thrown when the requested type is not registered in the current scope or any parent scope.</exception>
        private object ResolveInternal(Type type, Scope fromScope)
        {
            if (_registry.ClosedRegistry.TryGetValue(type, out List<RegistrationEntry> entries) && entries.Count > 0)
            {
                RegistrationEntry entry = entries[^1];
                return MaterializeFromEntry(entry, fromScope);
            }

            // Open-generic fallback: if 'type' is a closed generic and its open definition is registered here,
            // promote a closed RegistrationEntry on the fly and cache it in ClosedRegistry for subsequent resolves. Walks parent chain in order,
            // so a child-scope resolve can be served by a parent-scope open-generic registration without first promoting on the parent.
            if (type.IsGenericType)
            {
                Type openDefinition = type.GetGenericTypeDefinition();
                if (_registry.OpenRegistry.TryGetValue(openDefinition, out OpenGenericEntry openEntry))
                {
                    RegistrationEntry promoted = PromoteOpenGeneric(type, openEntry);
                    return MaterializeFromEntry(promoted, fromScope);
                }
            }

            return _parent != null
                ? _parent.ResolveInternal(type, fromScope)
                : throw new RegistrationNotFoundException(type);
        }

        /// <summary>
        /// Materializes an instance of the service described by the provided registration entry within the context of the requesting scope.
        /// Handles activation, lifecycle management, and runtime cycle detection for the service.
        /// </summary>
        /// <param name="entry">The registration entry containing details about the service implementation, lifetime, and pre-built instance, if applicable.</param>
        /// <param name="requestingScope">The scope within which the service is being resolved. Used for lifecycle tracking and dependency resolution context.</param>
        /// <returns>An instance of the service described by the registration entry.</returns>
        /// <exception cref="CyclicDependencyException">Thrown when a cyclic dependency is detected while resolving the service instance.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the service has an unknown or unsupported lifetime configuration.</exception>
        internal object MaterializeFromEntry(RegistrationEntry entry, Scope requestingScope)
        {
            // Pre-built singletons (RegisterInstance) bypass activation, caching, and disposal tracking
            if (entry.IsPreBuiltInstance) return entry.Instance;

            // Runtime cycle detection. Primary check is registration-time, but this catches
            // cycles introduced by factories or other late-bound activator paths.
            HashSet<Type> stack = _resolutionStack.Value;
            if (!stack.Add(entry.ImplType))
            {
                // Build the cycle path: current stack contents in iteration order, plus the offending type.
                List<Type> cyclePath = new List<Type>(stack.Count + 1);
                foreach (Type t in stack)
                {
                    cyclePath.Add(t);
                }

                cyclePath.Add(entry.ImplType);

                throw new CyclicDependencyException(cyclePath);
            }

            try
            {
                switch (entry.Lifetime)
                {
                    case Lifetime.Singleton:
                    {
                        Scope rootScope = Root;
                        lock (rootScope._lock)
                        {
                            if (rootScope._singletonCache.TryGetValue(entry, out object cached))
                                return cached;

                            object created = WrapDecorators(entry, () => entry.Activator(this));
                            rootScope._singletonCache[entry] = created;

                            if (created is IDisposable disposable) rootScope._disposables.Add(disposable);

                            return created;
                        }
                    }

                    case Lifetime.Scoped:
                    {
                        // The scope that "owns" a Scoped instance is where the registration lives.
                        // Walk up from "requestingScope" to find the first scope whose
                        // registry contains this entry. This makes child resolves of a parent-registered
                        // Scoped service share a single instance per the parent scope.
                        Scope owning = FindOwningScope(entry, requestingScope);
                        lock (owning._lock)
                        {
                            if (owning._scopedCache.TryGetValue(entry, out object cached))
                                return cached;

                            object created = WrapDecorators(entry, () => entry.Activator(requestingScope));
                            owning._scopedCache[entry] = created;

                            if (created is IDisposable disposable) owning._disposables.Add(disposable);

                            return created;
                        }
                    }

                    case Lifetime.Transient:
                    {
                        object created = WrapDecorators(entry, () => entry.Activator(requestingScope));

                        // Per planning decision: only IDisposable transients are tracked,
                        // attributed to the scope that requested them
                        if (created is IDisposable disposable)
                        {
                            lock (requestingScope._lock)
                            {
                                requestingScope._disposables.Add(disposable);
                            }
                        }

                        return created;
                    }

                    default: throw new InvalidOperationException($"Unknown lifetime '{entry.Lifetime}'.");
                }
            }
            finally
            {
                stack.Remove(entry.ImplType);
            }
        }

        /// <summary>
        /// Promotes an <see cref="OpenGenericEntry"/> to a closed <see cref="RegistrationEntry"/> for the requested closed service type,
        /// caches it in this scope's <see cref="Registry.ClosedRegistry"/>, and returns it.
        /// Lock-guarded so concurrent first-resolves of the same closed type don't race to add duplicate entries.
        /// </summary>
        /// <param name="closedServiceType">The closed generic service type being resolved (e.g., <c>IRepository&lt;MonsterDefinition&gt;</c>).</param>
        /// <param name="openEntry">The open-generic entry whose <see cref="OpenGenericEntry.OpenImplType"/> will be closed against the same type arguments.</param>
        /// <returns>The cached <see cref="RegistrationEntry"/> for <paramref name="closedServiceType"/>.</returns>
        private RegistrationEntry PromoteOpenGeneric(Type closedServiceType, OpenGenericEntry openEntry)
        {
            // Synchronize so concurrent first-resolves of the same closed type don't both add an entry.
            // Reuses the per-scope _lock that Singleton/Scoped paths already use.
            lock (_lock)
            {
                // Re-check under the lock: another thread may have promoted while we were blocked
                if (_registry.ClosedRegistry.TryGetValue(closedServiceType, out List<RegistrationEntry> existing) &&
                    existing.Count > 0)
                {
                    return existing[^1];
                }

                Type closedImpl = openEntry.OpenImplType.MakeGenericType(closedServiceType.GetGenericArguments());
                ExpressionActivatorFactory factory = new ExpressionActivatorFactory();
                RegistrationEntry entry = new RegistrationEntry(
                    serviceType: closedServiceType,
                    implType: closedImpl,
                    lifetime: openEntry.Lifetime,
                    activator: factory.Build(closedImpl),
                    instance: null
                );
                _registry.Add(entry);

                return entry;
            }
        }

        /// <summary>
        /// Applies registered decorators to the provided service instance, using the decorator chain
        /// associated with the service type in the registry. If no decorators are registered for the service
        /// type, the original instance is returned.
        /// </summary>
        /// <param name="entry">The registration entry specifying the service type and details for the instance being decorated.</param>
        /// <param name="buildInner">A delegate that creates the initial service instance before applying decorators.</param>
        /// <returns>The service instance wrapped with all applicable decorators, or the original instance if no decorators are registered.</returns>
        private object WrapDecorators(RegistrationEntry entry, Func<object> buildInner)
        {
            object inner = buildInner.Invoke();

            // Decorators apply only at the scope where they were registered
            if (!_registry.DecoratorChains.TryGetValue(entry.ServiceType, out List<DecoratorEntry> decorators)
                || decorators.Count == 0
               )
            {
                return inner;
            }

            object current = inner;
            for (int i = 0; i < decorators.Count; i++)
            {
                current = BuildDecorator(decorators[i], current);
            }

            return current;
        }

        /// <summary>
        /// Constructs a decorator instance by resolving its dependencies and injecting the inner service.
        /// </summary>
        /// <param name="decorator">The metadata describing the decorator, including its type and service type.</param>
        /// <param name="inner">The instance of the inner service to be wrapped by the decorator.</param>
        /// <returns>The constructed decorator instance wrapping the inner service.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the decorator metadata or inner service is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when no suitable constructor is found for the decorator type.</exception>
        private object BuildDecorator(DecoratorEntry decorator, object inner)
        {
            ConstructorInfo constructor = ConstructorSelector.SelectConstructor(decorator.DecoratorType);
            ParameterInfo[] parameters = constructor.GetParameters();

            object[] args = new object[parameters.Length];
            args[0] = inner;
            for (int i = 1; i < parameters.Length; i++)
            {
                args[i] = Resolve(parameters[i].ParameterType);
            }

            return constructor.Invoke(args);
        }

        /// <summary>
        /// Identifies the scope that "owns" a specific registration entry by traversing the parent chain
        /// from the provided starting scope. This ensures that Scoped services use the instance tied
        /// to the owning scope where the registration was originally defined.
        /// </summary>
        /// <param name="entry">The registration entry for which the owning scope is sought.</param>
        /// <param name="start">The scope from which to begin the search for the owning scope.</param>
        /// <returns>The scope that owns the provided registration entry. If no such scope is found, the starting scope is returned.</returns>
        private Scope FindOwningScope(RegistrationEntry entry, Scope start)
        {
            for (Scope s = start; s != null; s = s._parent)
            {
                if (!s._registry.ClosedRegistry.TryGetValue(entry.ServiceType, out List<RegistrationEntry> list) ||
                    !list.Contains(entry))
                    continue;

                return s;
            }

            return start;
        }

        /// <summary>
        /// Gathers all registration entries associated with the specified service type from the current scope
        /// and its parent scopes, if any, into a collection.
        /// </summary>
        /// <param name="serviceType">The type of service for which registration entries should be collected.</param>
        /// <returns>A list of tuples, where each tuple contains a <see cref="RegistrationEntry"/> and
        /// the <see cref="Scope"/> it was registered in.</returns>
        internal List<(RegistrationEntry entry, Scope scope)> CollectAllEntries(Type serviceType)
        {
            List<(RegistrationEntry, Scope)> all = new List<(RegistrationEntry, Scope)>();
            CollectInto(all, serviceType);
            return all;
        }

        /// <summary>
        /// Collects all registration entries of the specified service type from the current scope and its parent scopes
        /// into the provided bucket. Each entry is associated with the scope in which it was registered.
        /// </summary>
        /// <param name="bucket">The collection into which the registration entries and their corresponding scopes will be added.</param>
        /// <param name="serviceType">The type of service for which registration entries are being collected.</param>
        private void CollectInto(List<(RegistrationEntry, Scope)> bucket, Type serviceType)
        {
            _parent?.CollectInto(bucket, serviceType);

            if (!_registry.ClosedRegistry.TryGetValue(serviceType, out List<RegistrationEntry> entries)) return;

            for (int i = 0; i < entries.Count; i++)
            {
                bucket.Add((entries[i], this));
            }
        }

        /// <summary>
        /// Retrieves an enumeration of registries from the current scope's parent chain.
        /// Each registry represents the collection of service registrations and decorator chains
        /// for a specific scope in the hierarchy, starting from the immediate parent and moving up.
        /// </summary>
        /// <returns>An enumerable collection of <see cref="Registry"/> objects from the parent scopes,
        /// ordered from the immediate parent to the root scope.</returns>
        internal IEnumerable<Registry> ParentRegistries()
        {
            for (Scope s = _parent; s != null; s = s._parent)
            {
                yield return s._registry;
            }
        }

        /// <summary>
        /// Like <see cref="ParentRegistries"/>, but yields this scope's own registry first.
        /// Used by <see cref="CreateChildScope"/> when invoking <see cref="Registry.PrebuildPreservedClosedGenerics"/>
        /// on a child builder, so the child's preserved closed generics can resolve against an open registration declared on the
        /// immediate parent (i.e., this scope).
        /// </summary>
        /// <returns>
        /// An enumerable sequence of <see cref="Registry"/> instances starting with the current scope and traversing
        /// up the parent scope chain.
        /// </returns>
        internal IEnumerable<Registry> ParentRegistriesIncludingSelf()
        {
            yield return _registry;
            for (Scope s = _parent; s != null; s = s._parent)
            {
                yield return s._registry;
            }
        }

        /// <summary>
        /// Runs <see cref="IInitializable.Initialize"/> on every <see cref="IInitializable"/>
        /// instance currently materialized in this scope's cache plus the root-scope singleton cache.
        /// Idempotent; each instance is initialized at most once across repeated calls.
        /// </summary>
        /// <remarks>
        /// As per the Phase 1 / Phase 2 ordering: by the time this is called, all bindings
        /// (Phase 1) for the materialized instances are complete. <c>Initialize()</c> (Phase 2)
        /// can therefore safely observe peer instances' bound state.
        ///
        /// Transient instances are not tracked in either cache and therefore not visited here;
        /// register an IInitializable as Scoped or Singleton if y ou need its Initialize hook.
        /// </remarks>
        internal void RunInitializables()
        {
            ThrowIfDisposed();

            // Collect targets first, then invoke Initialize(); keeps cache iteration
            // separate from user code that might (in principle) cause re-entrancy
            List<object> targets = new List<object>();

            foreach (KeyValuePair<RegistrationEntry, object> pair in _scopedCache)
            {
                if (pair.Value is not IInitializable || !_initialized.Add(pair.Value))
                    continue;

                targets.Add(pair.Value);
            }

            // Singleton cache is only present on the root scope; harmless to check elsewhere
            if (_singletonCache != null)
            {
                foreach (KeyValuePair<RegistrationEntry, object> pair in _singletonCache)
                {
                    if (pair.Value is not IInitializable || !_initialized.Add(pair.Value))
                        continue;

                    targets.Add(pair.Value);
                }
            }

            for (int i = 0; i < targets.Count; i++)
            {
                ((IInitializable)targets[i]).Initialize();
            }
        }

        /// <summary>
        /// Walks this scope's registry in registration order, materializes any
        /// <see cref="IAsyncStartable"/> registrations, and awaits <c>StartAsync</c> on each
        /// sequentially. Each startable's await must complete before the next begins.
        /// </summary>
        /// <param name="cancellationToken">Cancellation propagated to each StartAsync call.</param>
        internal async Task RunAsyncStartablesAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            foreach (KeyValuePair<Type, List<RegistrationEntry>> kvp in _registry.ClosedRegistry)
            {
                // Only consider service-type buckets that are (or implement) IAsyncStartable
                if (!typeof(IAsyncStartable).IsAssignableFrom(kvp.Key)) continue;

                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    object instance = MaterializeFromEntry(kvp.Value[i], this);

                    if (instance is not IAsyncStartable startable) continue;

                    await startable.StartAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Disposes of all resources associated with the current scope instance, releasing any objects
        /// that were created during the lifetime of this scope. Ensures that resources are disposed
        /// in reverse order of creation and prevents further use of the scope after disposal.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown if operations are attempted on the scope after it has been disposed.</exception>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            // Snapshot children under the lock; cascade Dispose() calls hapen outside the lock
            // so a child's Dispose can re-enter via the parent's _lock when
            // it removes itself from _children. Calling Dispose on an already-disposed
            // child is a no-op (idempotent).
            Scope[] childSnapshot;
            lock (_lock)
            {
                childSnapshot = _children.ToArray();
            }

            for (int i = 0; i < childSnapshot.Length; i++)
            {
                try
                {
                    childSnapshot[i].Dispose();
                }
                catch
                {
                    // Swallow per-child: do not let one failed child Dispose
                    // block the rest of the cascade
                }
            }

            // Remove from parent's child list so a still-alive parent does not retain
            // a reference to a disposed scope (helps the GDC). Parent may already be null
            // on a root scope
            if (_parent != null)
            {
                lock (_parent._lock)
                {
                    _parent._children.Remove(this);
                }
            }

            // Reverse-creation order so dependents Dispose before their dependencies
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                try
                {
                    _disposables[i].Dispose();
                }
                catch
                {
                    // Swallow per-disposable: do not let one failed
                    // dispose block the rest of the chain
                }
            }

            _disposables.Clear();
            _scopedCache.Clear();
        }

        /// <summary>
        /// Ensures that the current <see cref="Scope"/> instance is not disposed before performing an action.
        /// Throws an exception if the instance has already been disposed.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the <see cref="Scope"/> has been disposed and an operation is attempted on it.
        /// </exception>
        private void ThrowIfDisposed()
        {
            if (!_disposed) return;

            throw new ObjectDisposedException(nameof(Scope));
        }
    }
}