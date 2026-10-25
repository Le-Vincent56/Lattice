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
        // The message of the AggregateException Dispose throws after a cleanup in which any step threw.
        private const string DisposalFailureMessage = "One or more objects threw while the scope was disposed; every other object was still disposed.";

        // The message of the ObjectDisposedException a resolve throws when the object it built was refused because its
        // scope's disposal began meanwhile, and disposing that object threw.
        private const string RefusedObjectMessage = "The scope was disposed while this object was being built; the object was disposed.";
        
        private readonly Scope _parent;
        private readonly Registry _registry;

        // Per-scope cache of Scoped implementation instances, keyed by the registration's primary entry so every
        // alias of one registration shares one instance.
        private readonly Dictionary<RegistrationEntry, object> _scopedCache = new Dictionary<RegistrationEntry, object>();

        // Per-scope cache of decorated Scoped results, keyed by the alias entry: each alias wraps the shared instance
        // in the decorator chain of its own service type.
        private readonly Dictionary<RegistrationEntry, object> _scopedDecoratedCache = new Dictionary<RegistrationEntry, object>();

        // All IDisposables created within this scope, in creation order. Disposed in reverse on Dispose().
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        // Per-thread set of implementation types currently being resolved on this scope's call chain.
        // Catches cycles the registration-time validator missed (e.g., factory-introduced cycles)
        private readonly ThreadLocal<HashSet<Type>> _resolutionStack = new ThreadLocal<HashSet<Type>>(() => new HashSet<Type>());

        private readonly object _lock = new object();

        // Singleton caches live only on the root scope; child scopes route Singleton lookups via Root. The first holds
        // implementation instances keyed by primary entry, the second decorated results keyed by alias entry.
        private readonly Dictionary<RegistrationEntry, object> _singletonCache;
        private readonly Dictionary<RegistrationEntry, object> _singletonDecoratedCache;

        // Instances this scope has initialized, compared by reference so two distinct instances that are equal by
        // Equals are two participants. An instance is added only after its Initialize returned.
        private readonly HashSet<object> _initialized = new HashSet<object>(ReferenceIdentityComparer.Instance);

        // Every cached instance and decorator wrapper this scope built, in the order each finished construction. The
        // initialization pass runs in this order, which puts a dependency before anything built with it and is the
        // reverse of disposal order.
        private readonly List<object> _creationOrder = new List<object>();

        // Where this scope is in its lifecycle. Written only under _lock. Volatile because the fast-path disposal checks
        // (ThrowIfDisposed on the public surface) read it without the lock; every decision that depends on it, such as
        // a tracking add or a lifecycle entry, re-reads it under _lock.
        private volatile LifecycleState _state = LifecycleState.Created;

        // The exception that faulted this scope; quoted by every later refused call.
        private Exception _fault;
        
        // The token source startup participants receive. Created by BeginStart and owned by this scope, not linked: the
        // caller's token reaches it only through _callerRegistration while startup runs. ShutdownAsync and Dispose
        // cancel it; only Dispose disposes it. Null before startup and after disposal. Read and written under _lock.
        private CancellationTokenSource _participantCancellation;

        // The caller token's registration that cancels _participantCancellation while startup runs. Removed when the
        // startup loop ends or by Dispose, whichever comes first; default when there is none. Read and written under
        // _lock.
        private CancellationTokenRegistration _callerRegistration;

        // Completed, never faulted, once the startup loop has left its finally; what ShutdownAsync waits for. Created by
        // BeginStart with RunContinuationsAsynchronously, so completing it never runs a waiter inline. Null before
        // startup. Read and written under _lock.
        private TaskCompletionSource<bool> _startupSettled;

        // Completed once the Dispose that latched has run every step; what a ShutdownAsync awaits when another caller
        // disposed this scope. Created with the scope, so it is always there to await.
        private readonly TaskCompletionSource<bool> _disposalCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Children registered against this scope; populated by their constructors and drained on Dispose (cascade). Walked
        // by DiagnosticsExtensions.DumpScopeTree to prin the scope tree root-down
        private readonly List<Scope> _children = new List<Scope>();

        // The scope whose startup loop is awaiting a StartAsync on this logical call chain. Set before each await, so it
        // flows into the participant and whatever it starts; the async method's own execution context keeps it from
        // reaching the caller. Read by ShutdownAsync to refuse a shutdown that would wait on itself.
        private static readonly AsyncLocal<Scope> _runningStartup = new AsyncLocal<Scope>();
        
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
        
        /// <summary>
        /// Where this scope is in its lifecycle: created, running its initialization pass, initialized, starting, ready,
        /// faulted by a failed pass or startup, stopping while a shutdown waits for running startup work, or disposed.
        /// </summary>
        internal LifecycleState State
        {
            get
            {
                lock (_lock)
                {
                    return _state;
                }
            }
        }
        
        /// <summary>
        /// Reads this scope's state and the exception that faulted it together, under <c>_lock</c>, so a child's
        /// refusal quotes the fault of the very state it saw. Called by a child under its own lock, in the
        /// child-then-parent order.
        /// </summary>
        /// <param name="fault">Receives the exception that faulted this scope, or null when it was never faulted.</param>
        /// <returns>This scope's state.</returns>
        internal LifecycleState ReadState(out Exception fault)
        {
            lock (_lock)
            {
                fault = _fault;
                return _state;
            }
        }

        public Scope(Registry registry, Scope parent)
        {
            _registry = registry;
            _parent = parent;

            // Only the root has Singleton caches; child scopes route Singleton
            // lookups via Root
            _singletonCache = parent == null
                ? new Dictionary<RegistrationEntry, object>()
                : null;
            _singletonDecoratedCache = parent == null
                ? new Dictionary<RegistrationEntry, object>()
                : null;

            if (parent == null) return;

            // Register with the parent so the parent's Dispose can cascade, under the parent's lock: the parent's
            // Dispose latches and snapshots its children under the same lock, so this child is either in that snapshot
            // or refused here, never added to a parent whose cascade has passed.
            lock (parent._lock)
            {
                if (parent._state == LifecycleState.Disposed) throw new ObjectDisposedException(nameof(Scope));

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

        /// <summary>
        /// Creates a child scope whose registrations shadow this scope's for resolves that start in the child.
        /// The child is validated before it exists: Singleton registrations are rejected, and the child's graph
        /// is walked against this scope and every ancestor so cycles surface here rather than at first resolve.
        /// </summary>
        /// <param name="configure">Registers the child's services on a fresh builder.</param>
        /// <returns>The new child scope.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when this scope has been disposed.</exception>
        /// <exception cref="ChildSingletonRegistrationException">Thrown when the child registers a service with <see cref="Lifetime.Singleton"/> other than through <c>RegisterInstance</c>.</exception>
        /// <exception cref="CyclicDependencyException">Thrown when the child's registrations form a cycle, alone or through this scope's and its ancestors' registrations.</exception>
        public IObjectResolver CreateChildScope(Action<IContainerBuilder> configure)
        {
            ThrowIfDisposed();
            ContainerBuilder childBuilder = new ContainerBuilder();
            configure.Invoke(childBuilder);

            // Promote PreserveClosedGenerics declarations into the child's closed registry first, so a closed
            // generic preserved against an open registration declared on this scope or an ancestor is both
            // validated and checked for lifetime below.
            childBuilder.Registry.PrebuildPreservedClosedGenerics(parentChain: ParentRegistriesIncludingSelf());

            // A Singleton in a child would be cached by the root and outlive the child. Reject it before the
            // graph walk so the error names the registration.
            ThrowIfChildRegistersSingleton(childBuilder.Registry);

            // Validate the child's registrations against this scope and every ancestor, nearest first. This is the
            // same chain Resolve walks, so a cycle that crosses into this scope, and a registration this scope
            // shadows, are judged exactly as resolution would see them, and surface here rather than at first resolve.
            DependencyGraphValidator.Validate(childBuilder.Registry, parentChain: ParentRegistriesIncludingSelf());

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
        /// Returns whether this scope still holds <paramref name="instance"/> in any of its tracking structures: either
        /// Scoped cache, the root's two Singleton caches, the creation order, the initialized set or the disposal list.
        /// Compares by reference. The registry, and so a <c>RegisterInstance</c> value, is not consulted. A test seam:
        /// retention is not observable through the public API.
        /// </summary>
        /// <param name="instance">The object to look for.</param>
        /// <returns>True when any tracking structure of this scope holds <paramref name="instance"/>.</returns>
        internal bool IsTracking(object instance)
        {
            lock (_lock)
            {
                if (ContainsReference(_scopedCache.Values, instance)) return true;
                if (ContainsReference(_scopedDecoratedCache.Values, instance)) return true;

                if (_singletonCache != null && ContainsReference(_singletonCache.Values, instance)) return true;
                if (_singletonDecoratedCache != null && ContainsReference(_singletonDecoratedCache.Values, instance)) return true;

                if (ContainsReference(_creationOrder, instance)) return true;
                if (_initialized.Contains(instance)) return true;

                return ContainsReference(_disposables, instance);
            }
        }
        
        /// <summary>
        /// Returns whether <paramref name="items"/> holds <paramref name="instance"/> itself, compared by reference.
        /// </summary>
        /// <param name="items">The structure to search.</param>
        /// <param name="instance">The object to look for.</param>
        /// <returns>True when an item is the same object as <paramref name="instance"/>.</returns>
        private static bool ContainsReference(IEnumerable<object> items, object instance)
        {
            foreach (object item in items)
            {
                if (ReferenceEquals(item, instance)) return true;
            }

            return false;
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
        /// <param name="requestingScope">
        /// The scope the public resolve was called on. Transient instances are built with it and, when disposable, tracked by it; Scoped and Singleton
        /// instances are built with and tracked by the scope that caches them.
        /// </param>
        /// <returns>An instance of the service described by the registration entry.</returns>
        /// <exception cref="CyclicDependencyException">Thrown when a cyclic dependency is detected while resolving the service instance.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the service has an unknown or unsupported lifetime configuration.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the scope that owns or tracks the object was disposed before or while it was built; an object built meanwhile was disposed.</exception>
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
                            // Dispose latches and snapshots under this lock: an activation that begins after the
                            // latch builds nothing.
                            rootScope.ThrowIfDisposed();

                            return GetOrCreateCached(
                                rootScope,
                                entry,
                                rootScope._singletonCache,
                                rootScope._singletonDecoratedCache,
                                rootScope._disposables,
                                rootScope._creationOrder
                            );
                        }
                    }

                    case Lifetime.Scoped:
                    {
                        // This scope's registry holds the entry, so this scope owns the instance: it caches it,
                        // builds it with its own resolution context, and disposes it. Activating with "this"
                        // rather than the requesting scope keeps a child registration from being captured by an
                        // instance the whole subtree shares, attributes any IDisposable Transients created
                        // during the activation to this scope so they outlive every child, and means nested
                        // locks during activation are only ever this scope's or an ancestor's.
                        lock (_lock)
                        {
                            ThrowIfDisposed();

                            return GetOrCreateCached(
                                this,
                                entry,
                                _scopedCache,
                                _scopedDecoratedCache,
                                _disposables,
                                _creationOrder
                            );
                        }
                    }

                    case Lifetime.Transient:
                    {
                        object inner = entry.Activator(requestingScope);
                        List<object> built = HasDecorators(entry) ? new List<object>() : null;
                        object created;
                        try
                        {
                            created = WrapDecorators(entry, inner, built);
                        }
                        catch
                        {
                            // The inner instance and the links built before the failure belong to the requesting scope
                            // all the same.
                            TrackTransient(requestingScope, inner, built);
                            throw;
                        }

                        TrackTransient(requestingScope, inner, built);
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
        /// Tracks what a Transient resolve built for disposal by the scope that requested it: the inner instance, then
        /// each decorator in build order, each only when it is <see cref="IDisposable"/>. A resolve that built nothing
        /// disposable takes no lock. When the requesting scope's disposal has begun, nothing is added: the objects are
        /// disposed outside the lock, last built first, and the resolve fails.
        /// </summary>
        /// <param name="requestingScope">The scope the public resolve was called on.</param>
        /// <param name="inner">The implementation instance.</param>
        /// <param name="built">The decorators built around it, in build order, or null when the service has none.</param>
        /// <exception cref="ObjectDisposedException">Thrown when <paramref name="requestingScope"/> was disposed while the objects were built; they were disposed.</exception>
        private void TrackTransient(Scope requestingScope, object inner, List<object> built)
        {
            // Only disposable objects are tracked, by the scope that requested the resolve.
            bool ownsDisposable = inner is IDisposable;
            if (built != null)
            {
                for (int i = 0; i < built.Count; i++)
                {
                    if (built[i] is IDisposable) ownsDisposable = true;
                }
            }

            if (!ownsDisposable) return;

            bool refused;
            lock (requestingScope._lock)
            {
                refused = requestingScope._state == LifecycleState.Disposed;
                if (!refused)
                {
                    if (inner is IDisposable innerDisposable) requestingScope._disposables.Add(innerDisposable);

                    if (built != null)
                    {
                        for (int i = 0; i < built.Count; i++)
                        {
                            if (built[i] is IDisposable linkDisposable) requestingScope._disposables.Add(linkDisposable);
                        }
                    }
                }
            }

            if (!refused) return;

            // The requesting scope's disposal began while these objects were built and has already taken its snapshot:
            // dispose them here, outside the lock, and fail the resolve.
            List<object> refusedObjects = new List<object> { inner };
            if (built != null) refusedObjects.AddRange(built);

            throw DisposeRefusedObjects(refusedObjects);
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
        /// Applies the decorator chain registered in this scope for <paramref name="entry"/>'s service type to
        /// <paramref name="inner"/>, first registered innermost, and adds each decorator to <paramref name="built"/> as
        /// soon as it is built, so the caller can track every link of the chain. Returns <paramref name="inner"/>
        /// itself when the service type has no decorators here. When a decorator throws, <paramref name="built"/> holds
        /// the links built before it.
        /// </summary>
        /// <param name="entry">The entry being resolved; its service type selects the decorator chain.</param>
        /// <param name="inner">The built implementation instance to wrap.</param>
        /// <param name="built">Receives each decorator in build order, innermost first; null when the caller tracks none.</param>
        /// <returns>The outermost decorator, or <paramref name="inner"/> when no decorators are registered.</returns>
        private object WrapDecorators(RegistrationEntry entry, object inner, List<object> built)
        {
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
                built?.Add(current);
            }

            return current;
        }
        
        /// <summary>
        /// Returns whether this scope registers a non-empty decorator chain for <paramref name="entry"/>'s service type,
        /// so a caller allocates a list for the chain's links only when there are links to hold.
        /// </summary>
        /// <param name="entry">The entry being resolved.</param>
        /// <returns>True when at least one decorator applies to the entry in this scope.</returns>
        private bool HasDecorators(RegistrationEntry entry)
        {
            return _registry.DecoratorChains.TryGetValue(entry.ServiceType, out List<DecoratorEntry> decorators) && decorators.Count > 0;
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
        /// Returns the cached result for <paramref name="entry"/>, building what is missing. The registration's
        /// implementation instance is built by and cached once under <see cref="RegistrationEntry.Primary"/>, so every
        /// alias shares it whichever alias is resolved first; an alias whose service type has a decorator chain in
        /// this scope gets that chain built once around the shared instance and cached under the alias entry. Every
        /// object of the chain is owned: the shared instance is tracked for disposal and appended to the creation order
        /// when created, then each decorator, innermost first, once the whole chain is built, so the pass initializes
        /// and startup starts the chain from the inside out and reverse-order disposal releases it outermost first.
        /// When a decorator throws, the links built before it are tracked for disposal only, never initialized or
        /// started, and the exception propagates. The caller holds the lock of <paramref name="owner"/>. A factory,
        /// constructor or decorator that disposes <paramref name="owner"/> does so on this thread inside that lock, so
        /// the state is re-read before each add: once <paramref name="owner"/> is disposed nothing is added, what this
        /// activation built is disposed, and the resolve fails.
        /// </summary>
        /// <param name="owner">The scope that owns both caches and both lists; its lock is held.</param>
        /// <param name="entry">The alias entry being resolved; it belongs to this scope's registry.</param>
        /// <param name="instanceCache">The implementation cache, keyed by primary entry.</param>
        /// <param name="decoratedCache">The decorated-result cache, keyed by alias entry.</param>
        /// <param name="disposables">The disposal list of <paramref name="owner"/>.</param>
        /// <param name="creationOrder">The creation-order list of <paramref name="owner"/>.</param>
        /// <returns>
        /// The decorated result for <paramref name="entry"/>, or the shared instance when its service type has no
        /// decorators.
        /// </returns>
        /// <exception cref="ObjectDisposedException">Thrown when <paramref name="owner"/> was disposed while this activation built an object; what it built was disposed.</exception>
        private object GetOrCreateCached(
            Scope owner,
            RegistrationEntry entry,
            Dictionary<RegistrationEntry, object> instanceCache,
            Dictionary<RegistrationEntry, object> decoratedCache,
            List<IDisposable> disposables,
            List<object> creationOrder
        )
        {
            if (decoratedCache.TryGetValue(entry, out object decorated)) return decorated;

            RegistrationEntry primary = entry.Primary;
            if (!instanceCache.TryGetValue(primary, out object instance))
            {
                instance = primary.Activator(this);
                if (owner._state == LifecycleState.Disposed) throw DisposeRefusedObjects(new[] { instance });

                instanceCache[primary] = instance;
                creationOrder.Add(instance);

                if (instance is IDisposable disposable) disposables.Add(disposable);
            }

            if (!HasDecorators(entry)) return instance;

            List<object> built = new List<object>();
            object wrapped;
            try
            {
                wrapped = WrapDecorators(entry, instance, built);
            }
            catch
            {
                // The links built before the failure are owned all the same: disposed with the scope, never initialized
                // or started. A scope disposed meanwhile takes none of them, so they are disposed here.
                if (owner._state == LifecycleState.Disposed)
                {
                    DisposeRefusedObjects(built);
                    throw;
                }

                for (int i = 0; i < built.Count; i++)
                {
                    if (built[i] is IDisposable builtDisposable) disposables.Add(builtDisposable);
                }

                throw;
            }

            if (owner._state == LifecycleState.Disposed) throw DisposeRefusedObjects(built);

            decoratedCache[entry] = wrapped;
            for (int i = 0; i < built.Count; i++)
            {
                creationOrder.Add(built[i]);

                if (built[i] is IDisposable linkDisposable) disposables.Add(linkDisposable);
            }

            return wrapped;
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
        /// Yields this scope's registry, then each ancestor's up to the root. This is the chain a child's
        /// preserved closed generics are prebuilt against and the chain the child's registrations are validated
        /// against, so both see the same registrations resolution from the child would see.
        /// </summary>
        /// <returns>The registries from this scope up to the root, nearest first.</returns>
        internal IEnumerable<Registry> ParentRegistriesIncludingSelf()
        {
            yield return _registry;
            for (Scope s = _parent; s != null; s = s._parent)
            {
                yield return s._registry;
            }
        }

        /// <summary>
        /// Runs this scope's initialization pass: materializes every Singleton or Scoped registration of this scope
        /// whose implementation or decorator type implements <see cref="IInitializable"/> or
        /// <see cref="IAsyncStartable"/>, in registration order, then calls <see cref="IInitializable.Initialize"/>
        /// once, by reference, on every initializable instance this scope has cached, in creation order. Startup
        /// participants are built here, so startup builds nothing. The parent scope must have completed its own pass
        /// first. A thrown <c>Initialize</c> or a failed construction faults this scope, which then refuses every later
        /// pass; dispose it and build a new one. A pass after a successful pass initializes only instances cached since.
        /// Once startup has begun, the pass initializes nothing: it returns when no cached initializable is pending and
        /// throws when one created since is, so the caller initializes it with <see cref="InitializeInstance"/>.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown when this scope has been disposed, before or during the pass; a pass that observes the disposal stops before the next participant.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when this scope is shutting down, when a pass is already running on this scope, when this scope is faulted, when the parent scope has not completed its pass,
        /// or, once startup has begun, when a cached initializable created since is not initialized.
        /// </exception>
        /// <exception cref="LifecycleException">Thrown when a participant's <c>Initialize</c> throws; this scope is then faulted.</exception>
        internal void RunInitializables()
        {
            ThrowIfDisposed();
            if (!BeginPass()) return;

            try
            {
                MaterializeParticipants();

                List<object> targets = CollectTargets();
                for (int i = 0; i < targets.Count; i++)
                {
                    InitializeTarget(targets[i]);
                }
            }
            catch (Exception exception)
            {
                Fault(exception);
                throw;
            }

            // Faulted is terminal: a supplied-instance failure that raced with this pass must not be overwritten.
            lock (_lock)
            {
                if (_state == LifecycleState.Initializing) _state = LifecycleState.Initialized;
            }
        }
        
        /// <summary>
        /// Initializes an externally supplied object once in this scope: an object this scope has already initialized,
        /// through a pass or an earlier call, is not initialized again. Instances are compared by reference. Requires
        /// this scope to have completed its initialization pass, so an object bound to this scope's services is never
        /// initialized before them. A thrown <c>Initialize</c> faults this scope.
        /// </summary>
        /// <param name="instance">The object to initialize. Need not implement <see cref="IInitializable"/>.</param>
        /// <returns>True when <see cref="IInitializable.Initialize"/> ran; false when <paramref name="instance"/> is not <see cref="IInitializable"/> or this scope already initialized it.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when this scope has been disposed.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="instance"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when this scope is shutting down, has not completed its pass, is running one, or is faulted.</exception>
        /// <exception cref="LifecycleException">Thrown when the object's <c>Initialize</c> throws; this scope is then faulted.</exception>
        internal bool InitializeInstance(object instance)
        {
            ThrowIfDisposed();
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            lock (_lock)
            {
                ThrowIfStoppingOrDisposed();
                ThrowIfFaultedOrInitializing();

                if (_state == LifecycleState.Created)
                {
                    throw new InvalidOperationException(
                        "Run RunInitializables on this scope before initializing supplied instances, so they never " +
                        "initialize before the services they depend on."
                    );
                }

                // Adding first reserves the instance, so two concurrent calls for one object cannot both run it.
                if (instance is not IInitializable || !_initialized.Add(instance)) return false;
            }

            try
            {
                ((IInitializable)instance).Initialize();
            }
            catch (Exception exception)
            {
                LifecycleException failure = new LifecycleException(instance.GetType(), LifecyclePhase.Initialize, exception);

                lock (_lock)
                {
                    _initialized.Remove(instance);
                }

                Fault(failure);
                throw failure;
            }

            return true;
        }

        /// <summary>
        /// Moves this scope into <see cref="LifecycleState.Initializing"/> after checking that it is neither stopping
        /// nor disposed, that no pass is running, that this scope is not faulted, and that the parent, if any, has
        /// completed its own pass. The parent's state is read under the parent's lock, in the child-then-parent order
        /// <see cref="Dispose"/> already uses. Once startup has begun, the pass has nothing to build or initialize: the
        /// state stays as it is, and a cached initializable created since is reported instead.
        /// </summary>
        /// <returns>True when the pass runs; false when startup has begun and nothing is pending.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when this scope has been disposed.</exception>
        /// <exception cref="InvalidOperationException">Thrown when this scope is shutting down, when a pass is already running, when this scope is faulted, when the parent has not completed its pass (a stopping or disposed parent included), or, once startup has begun, when a cached initializable is pending.</exception>
        private bool BeginPass()
        {
            lock (_lock)
            {
                ThrowIfStoppingOrDisposed();
                ThrowIfFaultedOrInitializing();

                if (_state == LifecycleState.Starting || _state == LifecycleState.Ready)
                {
                    ThrowIfInitializationPending();
                    return false;
                }

                if (_parent != null)
                {
                    LifecycleState parentState = _parent.ReadState(out Exception parentFault);
                    if (parentState == LifecycleState.Faulted)
                    {
                        throw new InvalidOperationException(
                            $"The parent scope is faulted and must be disposed and rebuilt: {parentFault.Message}",
                            parentFault
                        );
                    }

                    if (!HasCompletedPass(parentState))
                    {
                        throw new InvalidOperationException(
                            $"Run RunInitializables on the parent scope before the child: the parent's state is {parentState}."
                        );
                    }
                }

                _state = LifecycleState.Initializing;
                return true;
            }
        }

        /// <summary>
        /// Reports, once startup has begun, a cached <see cref="IInitializable"/> this scope has not initialized: an
        /// object created after the pass, which the closed pass will not initialize. The caller holds <c>_lock</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown for the first such object, in creation order.</exception>
        private void ThrowIfInitializationPending()
        {
            for (int i = 0; i < _creationOrder.Count; i++)
            {
                object candidate = _creationOrder[i];
                if (candidate is not IInitializable || _initialized.Contains(candidate)) continue;

                throw new InvalidOperationException(
                    $"'{candidate.GetType().FullName}' was created after the last initialization pass and is not " +
                    "initialized; once startup has begun the pass initializes nothing, so initialize it with " +
                    "InitializeInstance."
                );
            }
        }

        /// <summary>
        /// Rejects a lifecycle call while a pass is running or after this scope has faulted. The caller holds
        /// <c>_lock</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when this scope is initializing or faulted.</exception>
        private void ThrowIfFaultedOrInitializing()
        {
            if (_state == LifecycleState.Initializing)
            {
                throw new InvalidOperationException(
                    "RunInitializables is already running on this scope; a pass cannot start concurrently or from " +
                    "inside an Initialize body."
                );
            }

            ThrowIfFaulted();
        }

        /// <summary>
        /// Rejects a lifecycle call after a pass, a supplied object or startup has faulted this scope, quoting the
        /// first fault. The caller holds <c>_lock</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when this scope is faulted.</exception>
        private void ThrowIfFaulted()
        {
            if (_state != LifecycleState.Faulted) return;

            throw new InvalidOperationException(
                $"This scope is faulted and must be disposed and rebuilt: {_fault.Message}",
                _fault
            );
        }

        /// <summary>
        /// Returns whether a scope in <paramref name="state"/> has completed its initialization pass, so a child's
        /// pass may run.
        /// </summary>
        /// <param name="state">The parent's state.</param>
        /// <returns>True for <see cref="LifecycleState.Initialized"/>, <see cref="LifecycleState.Starting"/> and <see cref="LifecycleState.Ready"/>.</returns>
        private static bool HasCompletedPass(LifecycleState state)
        {
            return state is LifecycleState.Initialized or LifecycleState.Starting or LifecycleState.Ready;
        }

        /// <summary>
        /// Materializes every registration of this scope that takes part in the lifecycle, in registration order,
        /// through the same path a resolve uses. Pre-built instances and Transients never take part; a Singleton or
        /// Scoped entry takes part when its implementation type, or a decorator type registered for its service type
        /// in this scope, implements <see cref="IInitializable"/> or <see cref="IAsyncStartable"/>. Open-generic
        /// registrations are not walked; a closed generic takes part once it is preserved or promoted.
        /// </summary>
        private void MaterializeParticipants()
        {
            List<RegistrationEntry> entries = _registry.RegistrationOrder;
            for (int i = 0; i < entries.Count; i++)
            {
                RegistrationEntry entry = entries[i];
                if (entry.IsPreBuiltInstance || entry.Lifetime == Lifetime.Transient) continue;
                if (!IsLifecycleRegistration(entry)) continue;

                MaterializeFromEntry(entry, this);
            }
        }

        /// <summary>
        /// Returns whether <paramref name="entry"/>'s implementation type, or any decorator type registered in this
        /// scope for its service type, implements <see cref="IInitializable"/> or <see cref="IAsyncStartable"/>.
        /// </summary>
        /// <param name="entry">A closed registration of this scope.</param>
        /// <returns>True when the pass builds the registration.</returns>
        private bool IsLifecycleRegistration(RegistrationEntry entry)
        {
            if (IsLifecycleType(entry.ImplType)) return true;

            if (!_registry.DecoratorChains.TryGetValue(entry.ServiceType, out List<DecoratorEntry> decorators))
                return false;

            for (int i = 0; i < decorators.Count; i++)
            {
                if (IsLifecycleType(decorators[i].DecoratorType)) return true;
            }

            return false;
        }

        /// <summary>
        /// Returns whether <paramref name="type"/> implements a hook the pass or startup calls.
        /// </summary>
        /// <param name="type">An implementation or decorator type.</param>
        /// <returns>True when <paramref name="type"/> implements <see cref="IInitializable"/> or <see cref="IAsyncStartable"/>.</returns>
        private static bool IsLifecycleType(Type type)
        {
            return typeof(IInitializable).IsAssignableFrom(type) || typeof(IAsyncStartable).IsAssignableFrom(type);
        }

        /// <summary>
        /// Snapshots, under <c>_lock</c>, every <see cref="IInitializable"/> this scope has cached and not yet
        /// initialized, in creation order and once each even when one object sits behind several registrations.
        /// </summary>
        /// <returns>The instances to initialize, in the order to initialize them.</returns>
        private List<object> CollectTargets()
        {
            List<object> targets = new List<object>();
            HashSet<object> seen = new HashSet<object>(ReferenceIdentityComparer.Instance);

            lock (_lock)
            {
                for (int i = 0; i < _creationOrder.Count; i++)
                {
                    object candidate = _creationOrder[i];
                    if (candidate is not IInitializable || _initialized.Contains(candidate) || !seen.Add(candidate))
                        continue;

                    targets.Add(candidate);
                }
            }

            return targets;
        }

        /// <summary>
        /// Calls <see cref="IInitializable.Initialize"/> on <paramref name="target"/> outside the lock and records it
        /// as initialized only once the call returned. Refuses first, under the lock, when this scope is stopping or
        /// disposed, so a pass that observes a shutdown or disposal stops before this participant. An
        /// <c>Initialize</c> body that disposed this scope leaves nothing recorded.
        /// </summary>
        /// <param name="target">An <see cref="IInitializable"/> this scope owns.</param>
        /// <exception cref="ObjectDisposedException">Thrown when this scope has been disposed.</exception>
        /// <exception cref="InvalidOperationException">Thrown when this scope is shutting down.</exception>
        /// <exception cref="LifecycleException">Thrown, wrapping the hook's exception, when <c>Initialize</c> throws.</exception>
        private void InitializeTarget(object target)
        {
            lock (_lock)
            {
                ThrowIfStoppingOrDisposed();
            }

            try
            {
                ((IInitializable)target).Initialize();
            }
            catch (Exception exception)
            {
                throw new LifecycleException(target.GetType(), LifecyclePhase.Initialize, exception);
            }

            lock (_lock)
            {
                if (_state != LifecycleState.Disposed) _initialized.Add(target);
            }
        }

        /// <summary>
        /// Records the exception that ended this scope's pass, a supplied object's initialization or its startup, and
        /// marks a live scope faulted, so every later lifecycle call is refused with that exception quoted. The first
        /// fault is kept: a later one is not recorded. A scope that is stopping or disposed records the first fault
        /// too but keeps its state, because disposal is the only way on from there.
        /// </summary>
        /// <param name="exception">The exception that faulted this scope.</param>
        private void Fault(Exception exception)
        {
            lock (_lock)
            {
                _fault ??= exception;

                if (_state is LifecycleState.Faulted or LifecycleState.Stopping or LifecycleState.Disposed) return;

                _state = LifecycleState.Faulted;
            }
        }

        /// <summary>
        /// Runs this scope's startup once: starts every <see cref="IAsyncStartable"/> this scope has cached, once each
        /// by reference, in creation order, awaiting each <c>StartAsync</c> before calling the next, then moves this
        /// scope to <see cref="LifecycleState.Ready"/>. Builds nothing: the initialization pass built the participants.
        /// Participants receive a token this scope owns, which lives until disposal: <paramref name="cancellationToken"/>
        /// cancels it only while startup runs, and <c>ShutdownAsync</c> and <see cref="Dispose"/> cancel it at any time
        /// until then. The token, a shutdown and disposal are checked before each participant and before this scope
        /// becomes ready. Continues on the caller's synchronization context between participants, so a caller on a main
        /// thread keeps every <c>StartAsync</c> call on that thread. A call on a ready scope starts nothing. When the
        /// loop ends, however it ends, it signals that startup has settled, which is what <c>ShutdownAsync</c> waits for.
        /// </summary>
        /// <param name="cancellationToken">Cancels startup before or between participants; while startup runs it also cancels the token participants receive.</param>
        /// <returns>A task that completes when every participant has started and this scope is ready.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when this scope has been disposed before startup began.</exception>
        /// <exception cref="OperationCanceledException">
        /// Thrown when the token is cancelled or this scope is shut down or disposed; once startup has begun, a scope
        /// that was not shut down or disposed is then faulted.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when this scope is shutting down, when startup is already running, when this scope is faulted or has
        /// not completed its pass, when a cached participant is not initialized, or when the parent is not ready.
        /// </exception>
        /// <exception cref="LifecycleException">Thrown when a participant's <c>StartAsync</c> throws, returns a faulted task or returns no task; this scope is then faulted.</exception>
        internal async Task RunAsyncStartablesAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            List<object> targets = BeginStart(cancellationToken, out CancellationToken startupToken);
            if (targets == null) return;

            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    ThrowIfStartupStopped(startupToken);

                    object target = targets[i];
                    try
                    {
                        // A ShutdownAsync the participant calls on this scope, or on an ancestor, would wait for this
                        // very loop; the marker lets it refuse instead.
                        _runningStartup.Value = this;
                        await ((IAsyncStartable)target).StartAsync(startupToken);
                    }
                    catch (Exception exception)
                        when (exception is not OperationCanceledException || !startupToken.IsCancellationRequested)
                    {
                        LifecycleException failure = new LifecycleException(target.GetType(), LifecyclePhase.Start, exception);
                        Fault(failure);
                        throw failure;
                    }
                }

                CompleteStart(startupToken);
            }
            catch (OperationCanceledException exception)
            {
                Fault(exception);
                throw;
            }
            finally
            {
                ReleaseStartup();
            }
        }

        /// <summary>
        /// Snapshots, under <c>_lock</c>, every <see cref="IAsyncStartable"/> this scope has cached, in creation order
        /// and once each even when one object sits behind several registrations. Refuses the snapshot when an
        /// <see cref="IInitializable"/> among them has not been initialized, so nothing starts before its
        /// initialization.
        /// </summary>
        /// <returns>The instances to start, in the order to start them.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a cached participant is <see cref="IInitializable"/> and not yet initialized.</exception>
        private List<object> CollectStartTargets()
        {
            List<object> targets = new List<object>();
            HashSet<object> seen = new HashSet<object>(ReferenceIdentityComparer.Instance);

            lock (_lock)
            {
                for (int i = 0; i < _creationOrder.Count; i++)
                {
                    object candidate = _creationOrder[i];
                    if (candidate is not IAsyncStartable || !seen.Add(candidate)) continue;

                    if (candidate is IInitializable && !_initialized.Contains(candidate))
                    {
                        throw new InvalidOperationException(
                            $"'{candidate.GetType().FullName}' was created after the initialization pass and " +
                            "is not initialized; run RunInitializables before RunAsyncStartablesAsync.");
                    }

                    targets.Add(candidate);
                }
            }

            return targets;
        }
        
                /// <summary>
        /// Moves this scope into <see cref="LifecycleState.Starting"/> after its checks, in this order: neither
        /// stopping nor disposed (so a scope being shut down never reports that it has nothing to start); already
        /// ready (returns null, nothing to start); not faulted; not already starting; token not cancelled (no state
        /// change); pass completed; parent, if any, ready (read under the parent's lock, child-then-parent). Then
        /// snapshots the participants and creates, all or nothing, the cancellation source they receive, the caller
        /// token's registration that cancels it, and the settle signal: when any call throws, this scope is left as it
        /// was.
        /// </summary>
        /// <param name="cancellationToken">The caller's token.</param>
        /// <param name="startupToken">Receives the token participants are given; <see cref="CancellationToken.None"/> when null is returned.</param>
        /// <returns>The participants to start, in order, or null when this scope is already ready.</returns>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when this scope has been disposed, or, on some runtimes, when the source of
        /// <paramref name="cancellationToken"/> was disposed by the caller.
        /// </exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is already cancelled.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when this scope is shutting down, is faulted, already starting, has not completed its pass, holds an uninitialized participant, or
        /// when the parent is not ready.
        /// </exception>
        private List<object> BeginStart(CancellationToken cancellationToken, out CancellationToken startupToken)
        {
            startupToken = CancellationToken.None;

            lock (_lock)
            {
                ThrowIfStoppingOrDisposed();

                if (_state == LifecycleState.Ready) return null;

                ThrowIfFaulted();

                if (_state == LifecycleState.Starting)
                {
                    throw new InvalidOperationException(
                        "RunAsyncStartablesAsync is already running on this scope; startup cannot start " +
                        "concurrently or from inside a StartAsync body."
                    );
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (_state != LifecycleState.Initialized)
                {
                    throw new InvalidOperationException(
                        $"Run RunInitializables on this scope before RunAsyncStartablesAsync: the scope's state is {_state}."
                    );
                }

                if (_parent != null)
                {
                    LifecycleState parentState = _parent.ReadState(out Exception parentFault);
                    if (parentState == LifecycleState.Faulted)
                    {
                        throw new InvalidOperationException(
                            $"The parent scope is faulted and must be disposed and rebuilt: {parentFault.Message}",
                            parentFault
                        );
                    }

                    if (parentState != LifecycleState.Ready)
                    {
                        throw new InvalidOperationException(
                            $"Run RunAsyncStartablesAsync on the parent scope before the child: the parent's state is {parentState}."
                        );
                    }
                }

                List<object> targets = CollectStartTargets();

                // Build in locals and assign only once every call that can throw has returned: Register throws on some
                // runtimes for a token whose source the caller disposed, and a settle signal that no loop completes
                // would hang ShutdownAsync.
                CancellationTokenSource participantCancellation = new CancellationTokenSource();
                CancellationTokenRegistration callerRegistration = default;
                if (cancellationToken.CanBeCanceled)
                {
                    try
                    {
                        callerRegistration = cancellationToken.Register(static state =>
                        {
                            try
                            {
                                ((CancellationTokenSource)state).Cancel();
                            }
                            catch (ObjectDisposedException)
                            {
                                // Dispose cancelled and disposed the source while this callback was already running.
                            }
                        }, participantCancellation);
                    }
                    catch
                    {
                        participantCancellation.Dispose();
                        throw;
                    }
                }

                _participantCancellation = participantCancellation;
                _callerRegistration = callerRegistration;
                _startupSettled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                startupToken = participantCancellation.Token;
                _state = LifecycleState.Starting;

                return targets;
            }
        }

        /// <summary>
        /// Stops startup before the next participant, or before this scope becomes ready, when this scope faulted
        /// meanwhile, when the startup token is cancelled, or when a shutdown or <see cref="Dispose"/> has begun. Reads
        /// the state under <c>_lock</c>, so a shutdown or disposal that latched but has not yet cancelled the token
        /// still stops startup.
        /// </summary>
        /// <param name="startupToken">The token participants receive.</param>
        /// <exception cref="InvalidOperationException">Thrown when this scope is faulted.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the token is cancelled or this scope is stopping or disposed.</exception>
        private void ThrowIfStartupStopped(CancellationToken startupToken)
        {
            lock (_lock)
            {
                ThrowIfFaulted();

                bool stopped = _state is LifecycleState.Stopping or LifecycleState.Disposed;
                if (stopped || startupToken.IsCancellationRequested) throw new OperationCanceledException(startupToken);
            }
        }

        /// <summary>
        /// Moves this scope from <see cref="LifecycleState.Starting"/> to <see cref="LifecycleState.Ready"/> after the
        /// last participant, under the same lock as the final stop check, so a scope that faulted, was cancelled, or is
        /// stopping or disposed never becomes ready.
        /// </summary>
        /// <param name="startupToken">The token participants received.</param>
        /// <exception cref="InvalidOperationException">Thrown when this scope is faulted.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the token is cancelled or this scope is stopping or disposed.</exception>
        private void CompleteStart(CancellationToken startupToken)
        {
            lock (_lock)
            {
                ThrowIfStartupStopped(startupToken);
                _state = LifecycleState.Ready;
            }
        }

        /// <summary>
        /// Ends the running startup's hold on the caller's token and signals that startup has settled: takes the caller
        /// registration under <c>_lock</c> unless <see cref="Dispose"/> already took it, removes it outside the lock,
        /// then completes the settle signal. The participant source stays alive until <see cref="Dispose"/>. Removing
        /// the registration waits only when its callback is running on another thread at that moment.
        /// </summary>
        private void ReleaseStartup()
        {
            CancellationTokenRegistration callerRegistration;
            TaskCompletionSource<bool> settled;
            lock (_lock)
            {
                callerRegistration = _callerRegistration;
                _callerRegistration = default;
                settled = _startupSettled;
            }

            callerRegistration.Dispose();
            settled.TrySetResult(true);
        }

        /// <summary>
        /// Shuts this scope down in order, continuing on the caller's synchronization context: refuses a call made from
        /// this scope's own running startup or a descendant's; on a disposed scope waits for that disposal to finish;
        /// otherwise marks this scope and its descendants stopping, cancels their participant tokens, waits until every
        /// startup running in the subtree has settled or <paramref name="cancellationToken"/> is cancelled, whichever is
        /// first, then disposes this scope. A concurrent call joins the same shutdown; a disposal another caller
        /// latched reports its failures to that caller, and this call completes once it has finished.
        /// </summary>
        /// <param name="cancellationToken">Bounds the wait for running startup work; it never stops the disposal.</param>
        /// <returns>A task that completes once this scope is disposed.</returns>
        /// <exception cref="InvalidOperationException">Thrown, through the task and without changing any state, when called from inside a running startup of this scope or a descendant.</exception>
        /// <exception cref="AggregateException">Thrown, through the task, when a cancellation callback or the disposal threw; the scope is disposed.</exception>
        /// <exception cref="OperationCanceledException">Thrown, through the task, when <paramref name="cancellationToken"/> stopped the wait; the scope is disposed.</exception>
        internal async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            if (IsInsideOwnStartup())
            {
                throw new InvalidOperationException(
                    "ShutdownAsync cannot wait for a startup it is called from; call Dispose, or shut down from the " +
                    "scope's owner."
                );
            }

            if (_state == LifecycleState.Disposed)
            {
                await _disposalCompleted.Task;
                return;
            }

            List<CancellationTokenSource> sources = new List<CancellationTokenSource>();
            List<Task> settles = new List<Task>();
            BeginStopping(sources, settles);

            // Cancel outside every lock. A callback's failure is reported. An ObjectDisposedException means a callback
            // disposed the scope, and so this source, during this very call, which had already cancelled it.
            List<Exception> failures = new List<Exception>();
            for (int i = 0; i < sources.Count; i++)
            {
                try
                {
                    sources[i].Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Cancelled before a callback's disposal disposed it.
                }
                catch (Exception exception)
                {
                    AddFailures(failures, exception);
                }
            }

            bool settled = await WaitForSettleAsync(settles, cancellationToken);

            // A Dispose another caller latched first returns at once here; its failures went to that caller, and the
            // await below waits for it to finish. When this call latched, the signal is already complete.
            try
            {
                Dispose();
            }
            catch (AggregateException exception)
            {
                failures.AddRange(exception.InnerExceptions);
            }

            await _disposalCompleted.Task;

            if (failures.Count > 0) throw new AggregateException(DisposalFailureMessage, failures);

            if (!settled) throw new OperationCanceledException(cancellationToken);
        }
        
        /// <summary>
        /// Moves this scope and, recursively, its children to <see cref="LifecycleState.Stopping"/>, taking one lock at
        /// a time: under each scope's lock it skips a disposed scope, marks a live one stopping (a faulted scope keeps
        /// its state, which already refuses every lifecycle call), takes its participant token source and settle
        /// signal, and snapshots its children, then releases that lock before visiting them. A scope already stopping
        /// is joined: its source and signal are taken again, so a second shutdown waits for the same startups.
        /// </summary>
        /// <param name="sources">Receives each participant token source to cancel.</param>
        /// <param name="settles">Receives each settle signal to wait for.</param>
        private void BeginStopping(List<CancellationTokenSource> sources, List<Task> settles)
        {
            Scope[] children;
            lock (_lock)
            {
                if (_state == LifecycleState.Disposed) return;

                if (_state != LifecycleState.Faulted) _state = LifecycleState.Stopping;

                if (_participantCancellation != null) sources.Add(_participantCancellation);
                if (_startupSettled != null) settles.Add(_startupSettled.Task);

                children = _children.ToArray();
            }

            for (int i = 0; i < children.Length; i++)
            {
                children[i].BeginStopping(sources, settles);
            }
        }

        /// <summary>
        /// Returns whether the calling code runs inside a startup of this scope, or of a descendant, that has not
        /// settled, so a shutdown would wait on itself. Walks from the running-startup marker up the parent chain;
        /// the marker also flows into work a participant started without awaiting it, which is refused the same way
        /// until that startup settles.
        /// </summary>
        /// <returns>True when the marker names this scope or a descendant whose startup is still running.</returns>
        private bool IsInsideOwnStartup()
        {
            Scope running = _runningStartup.Value;
            for (Scope scope = running; scope != null; scope = scope._parent)
            {
                if (!ReferenceEquals(scope, this)) continue;

                TaskCompletionSource<bool> settled;
                lock (running._lock)
                {
                    settled = running._startupSettled;
                }

                return settled != null && !settled.Task.IsCompleted;
            }

            return false;
        }

        /// <summary>
        /// Waits until every settle signal in <paramref name="settles"/> has completed, or until
        /// <paramref name="cancellationToken"/> is cancelled, whichever comes first, continuing on the caller's
        /// synchronization context. Returns synchronously when there is nothing to wait for and when the token is
        /// already cancelled, so a shutdown with a cancelled token disposes within the call. A token whose source the
        /// caller disposed without cancelling it can never end the wait: when registering on it throws, the wait covers
        /// the settle signals alone, so the shutdown still disposes.
        /// </summary>
        /// <param name="settles">The settle signals taken from the subtree; they complete and never fault.</param>
        /// <param name="cancellationToken">Ends the wait early.</param>
        /// <returns>True when every signal completed; false when the token ended the wait first.</returns>
        private static async Task<bool> WaitForSettleAsync(List<Task> settles, CancellationToken cancellationToken)
        {
            if (settles.Count == 0) return true;

            Task all = Task.WhenAll(settles);
            if (all.IsCompleted) return true;

            if (cancellationToken.IsCancellationRequested) return false;

            if (!cancellationToken.CanBeCanceled)
            {
                await all;
                return true;
            }

            TaskCompletionSource<bool> canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration;
            try
            {
                registration = cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state).TrySetResult(true), canceled);
            }
            catch (ObjectDisposedException)
            {
                // Some runtimes refuse to register on a token whose source the caller disposed; that token can never be
                // cancelled, so only the settle signals end the wait.
                await all;
                return true;
            }

            try
            {
                Task first = await Task.WhenAny(all, canceled.Task);
                return ReferenceEquals(first, all);
            }
            finally
            {
                registration.Dispose();
            }
        }
        
        /// <summary>
        /// Disposes this scope and everything it owns, in order: detaches a running startup from the caller's token and
        /// cancels the token startup participants receive, disposes child scopes (each its own subtree first), removes
        /// this scope from its parent, disposes this scope's own disposables in reverse order of creation, then releases
        /// every cache, tracking entry and internal resource. Every step runs whatever an earlier one threw, and the
        /// failures are reported together afterwards. The scope is disposed either way, a second call does nothing, and
        /// an object built after disposal began is disposed rather than tracked. Does not wait for running startup
        /// work; <c>ShutdownAsync</c> does. The registry, and so every <c>RegisterInstance</c> value, is kept; nothing
        /// the caller supplied is disposed.
        /// </summary>
        /// <exception cref="AggregateException">Thrown after every step ran, when a child scope's disposal, a tracked object's Dispose or a cancellation callback threw.</exception>
        public void Dispose()
        {
            CancellationTokenSource participantCancellation;
            CancellationTokenRegistration callerRegistration;
            lock (_lock)
            {
                if (_state == LifecycleState.Disposed) return;

                _state = LifecycleState.Disposed;
                participantCancellation = _participantCancellation;
                callerRegistration = _callerRegistration;
                _callerRegistration = default;
            }

            List<Exception> failures = new List<Exception>();

            // Detach the caller's token first, so its later cancellation never reaches the source this call disposes;
            // then cancel the participants' token before anything is disposed, outside the lock so callbacks never run
            // under it.
            callerRegistration.Dispose();
            if (participantCancellation != null)
            {
                try
                {
                    participantCancellation.Cancel();
                }
                catch (Exception exception)
                {
                    AddFailures(failures, exception);
                }
            }

            // Snapshot children under the lock; each child's Dispose runs outside it, because a child re-enters this
            // lock to remove itself. A child's aggregate contributes its inner exceptions, so the list stays flat.
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
                catch (Exception exception)
                {
                    AddFailures(failures, exception);
                }
            }

            // Remove from the parent's child list so a still-alive parent does not retain a disposed scope.
            if (_parent != null)
            {
                lock (_parent._lock)
                {
                    _parent._children.Remove(this);
                }
            }

            // Take the disposal list under the lock: an add that raced with the latch is either in this copy or was
            // refused. Dispose in reverse creation order, outside the lock, so dependents go before their dependencies.
            // A service's own exception, an AggregateException included, is one entry.
            IDisposable[] disposables;
            lock (_lock)
            {
                disposables = _disposables.ToArray();
                _disposables.Clear();
            }

            for (int i = disposables.Length - 1; i >= 0; i--)
            {
                try
                {
                    disposables[i].Dispose();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            // Release everything else this scope tracked, and the participant source, which was cancelled above. The
            // settle signal stays: a startup loop still running completes it when its participant returns.
            lock (_lock)
            {
                _scopedCache.Clear();
                _scopedDecoratedCache.Clear();
                _singletonCache?.Clear();
                _singletonDecoratedCache?.Clear();
                _creationOrder.Clear();
                _initialized.Clear();
                _children.Clear();
                participantCancellation = _participantCancellation;
                _participantCancellation = null;
            }

            participantCancellation?.Dispose();
            _resolutionStack.Dispose();
            _disposalCompleted.TrySetResult(true);

            if (failures.Count > 0) throw new AggregateException(DisposalFailureMessage, failures);
        }

        /// <summary>
        /// Ensures that the current <see cref="Scope"/> instance is not disposed before performing an action.
        /// Throws an exception if the instance has already been disposed. Reads the state without the lock when called
        /// on the public surface; wherever a decision depends on it, the caller holds <c>_lock</c> or re-checks under it.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the <see cref="Scope"/> has been disposed and an operation is attempted on it.
        /// </exception>
        private void ThrowIfDisposed()
        {
            if (_state != LifecycleState.Disposed) return;

            throw new ObjectDisposedException(nameof(Scope));
        }
        
        /// <summary>
        /// Rejects a lifecycle call on a scope that is being shut down or has been disposed. The caller holds
        /// <c>_lock</c>.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown when this scope has been disposed.</exception>
        /// <exception cref="InvalidOperationException">Thrown when a shutdown is waiting on this scope.</exception>
        private void ThrowIfStoppingOrDisposed()
        {
            if (_state == LifecycleState.Disposed) throw new ObjectDisposedException(nameof(Scope));

            if (_state == LifecycleState.Stopping)
            {
                throw new InvalidOperationException(
                    "This scope is shutting down; lifecycle calls are refused until it is disposed."
                );
            }
        }

        /// <summary>
        /// Adds the failures <paramref name="exception"/> stands for to <paramref name="failures"/>. An
        /// <see cref="AggregateException"/> thrown by a child scope's disposal or by a cancellation's callbacks
        /// contributes its inner exceptions, so a disposal reports one flat list; any other exception is added as it
        /// is. A tracked object's own exception is added by the caller directly, so it stays one entry.
        /// </summary>
        /// <param name="failures">The failure list being collected.</param>
        /// <param name="exception">The exception a child scope's <c>Dispose</c> or a <c>Cancel</c> threw.</param>
        private static void AddFailures(List<Exception> failures, Exception exception)
        {
            if (exception is AggregateException aggregate)
            {
                failures.AddRange(aggregate.InnerExceptions);
                return;
            }

            failures.Add(exception);
        }

        /// <summary>
        /// Disposes, last built first, each <see cref="IDisposable"/> among <paramref name="refused"/>: objects an
        /// activation built after their scope's disposal began, which that disposal will never see. Every one is
        /// disposed whatever an earlier one threw. Returns the exception the resolve then throws.
        /// </summary>
        /// <param name="refused">The objects that were not tracked, in the order they were built.</param>
        /// <returns>
        /// An <see cref="ObjectDisposedException"/> naming the scope; when one of the disposals threw, it carries the
        /// first such exception as its inner exception.
        /// </returns>
        private static ObjectDisposedException DisposeRefusedObjects(IReadOnlyList<object> refused)
        {
            Exception firstFailure = null;
            for (int i = refused.Count - 1; i >= 0; i--)
            {
                if (refused[i] is not IDisposable disposable) continue;

                try
                {
                    disposable.Dispose();
                }
                catch (Exception exception)
                {
                    if (firstFailure == null) firstFailure = exception;
                }
            }

            return firstFailure == null
                ? new ObjectDisposedException(nameof(Scope))
                : new ObjectDisposedException(RefusedObjectMessage, firstFailure);
        }
        
        /// <summary>
        /// Rejects Singleton registrations in a child registry. The root caches and disposes every Singleton, so
        /// one registered in a child would be built from the child's registrations, outlive the child, and be
        /// rebuilt for every creation of that child. Pre-built instances are allowed: nothing builds or disposes them.
        /// </summary>
        /// <param name="childRegistry">The registry of the child about to be created, after preserved closed generics were prebuilt into it.</param>
        /// <exception cref="ChildSingletonRegistrationException">Thrown for the first closed or open Singleton registration found.</exception>
        private static void ThrowIfChildRegistersSingleton(Registry childRegistry)
        {
            for (int i = 0; i < childRegistry.RegistrationOrder.Count; i++)
            {
                RegistrationEntry entry = childRegistry.RegistrationOrder[i];
                if (entry.Lifetime != Lifetime.Singleton || entry.IsPreBuiltInstance) continue;

                throw new ChildSingletonRegistrationException(entry.ServiceType, entry.ImplType);
            }

            foreach (KeyValuePair<Type, OpenGenericEntry> pair in childRegistry.OpenRegistry)
            {
                if (pair.Value.Lifetime != Lifetime.Singleton) continue;

                throw new ChildSingletonRegistrationException(pair.Key, pair.Value.OpenImplType);
            }
        }
    }
}