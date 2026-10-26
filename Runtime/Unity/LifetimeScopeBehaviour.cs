using System;
using UnityEngine;

namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// Scene-attached host for an <see cref="IObjectResolver"/>.
    /// Acts as the entry point that wires every MonoBehaviour in its scene with constructor-
    /// and member-injected dependencies before any of them reach their <c>Start</c> callback.
    /// <list type="bullet">
    ///     <item>
    ///         <c>[DefaultExecutionOrder(int.MinValue + 100)]</c> runs this <c>Awake</c> early among scripts Unity
    ///         orders by execution order; a script with a lower order value still runs earlier
    ///     </item>
    ///     <item>
    ///         Two construction modes: a <see cref="Configure"/> override builds a self-owned root resolver, or
    ///         <see cref="SetResolver"/> accepts an externally built resolver, for example a child scope a coordinator
    ///         created after running the parent's initialization pass
    ///     </item>
    ///     <item>
    ///         In <c>Awake</c>: walks the scene's root GameObjects once, inactive ones included and
    ///         <see cref="ScopeBoundary"/> subtrees skipped, binds every component with injection members, runs
    ///         <see cref="ResolverLifecycleExtensions.RunInitializables"/> for the scope's own services, then initializes
    ///         the walk's <see cref="IInitializable"/> components once each, after the services they were bound to
    ///     </item>
    ///     <item>
    ///         In <c>OnDestroy</c>: disposes a self-built resolver and logs each cleanup failure as its own entry; never
    ///         throws. Externally provided resolvers are owned by their builder. <see cref="Resolver"/> is null
    ///         afterwards either way
    ///     </item>
    /// </list>
    /// </summary>
    [DefaultExecutionOrder(int.MinValue + 100)]
    public class LifetimeScopeBehaviour : MonoBehaviour
    {
        private IObjectResolver _resolver;

        // True when this behaviour built its own resolver via Container.Build(Configure); false when an external
        // resolver was supplied via SetResolver. Drives whether OnDestroy disposes the resolver; externally-owned resolvers
        // are disposed by their builder, not by this behaviour.
        private bool _ownsResolver;

        // Set by OnDestroy, so SetResolver keeps refusing a destroyed host once Resolver is cleared.
        private bool _destroyed;

        /// <summary>
        /// The active resolver. Null before <c>Awake</c> unless supplied through <see cref="SetResolver"/>, and null
        /// after <c>OnDestroy</c>, whoever owned it; keep your own reference to use it later. Consumers that need to
        /// inject late-spawned GameObjects should use <see cref="UnityResolverExtensions.InjectGameObject"/> against it.
        /// </summary>
        public IObjectResolver Resolver => _resolver;

        /// <summary>
        /// Builds the resolver (if not already supplied externally), then walks the scene's root GameObjects once, as
        /// they are at that moment, inactive ones included and <see cref="ScopeBoundary"/> subtrees skipped: binds
        /// every component with injection members, runs the scope's initialization pass, then initializes the walk's
        /// <see cref="IInitializable"/> components once each. A component added during the pass is in neither role;
        /// bind and initialize it explicitly with <c>InjectGameObject</c> and <c>InitializeGameObject</c>.
        /// </summary>
        /// <remarks>
        /// Subclasses that override <c>Awake</c> MUST call <c>base.Awake()</c> first; otherwise the scene walk and
        /// both initialization passes are skipped, and dependent components will see null <c>[Inject]</c> fields
        /// in their own Awake/Start callbacks. If the service pass throws, <c>Awake</c> throws before any component
        /// is initialized; a self-built resolver is still disposed in <c>OnDestroy</c>. It throws
        /// <see cref="ScopeOwnershipException"/> before binding anything when its scene holds another scope host
        /// outside a <see cref="ScopeBoundary"/>, or when it sits on or beneath a boundary itself.
        /// </remarks>
        /// <exception cref="ScopeOwnershipException">Thrown before anything is bound when the scene holds another scope host outside a boundary, or this host sits inside one; the message names each object and the fix.</exception>
        protected virtual void Awake()
        {
            if (_resolver == null)
            {
                // Self-build path: this behaviour is the composition root for the scene
                _resolver = Container.Build(Configure);
                _ownsResolver = true;
            }

            // One walk feeds both roles, so nothing is initialized that the walk did not find before the pass.
            InjectionTargets targets = ComponentDiscovery.Rent();
            try
            {
                ComponentDiscovery.CollectBatch(ScopeHostEngine.GetSceneRoots(gameObject), DiscoveryBatch.SceneRoots, this, targets);
                ScopeHostEngine.Prepare(_resolver, targets);
            }
            finally
            {
                ComponentDiscovery.Return(targets);
            }
        }
        
        /// <summary>
        /// Disposes the resolver only if this behaviour built it, and logs each cleanup failure as its own console
        /// entry instead of throwing, so a subclass's code after <c>base.OnDestroy()</c> always runs.
        /// Externally-provided resolvers are owned and disposed by whoever called <see cref="SetResolver"/>. Clears
        /// <see cref="Resolver"/> for either ownership.
        /// </summary>
        /// <remarks>
        /// A subclass that reads <see cref="Resolver"/> in its own <c>OnDestroy</c> reads it before calling
        /// <c>base.OnDestroy()</c>.
        /// </remarks>
        protected virtual void OnDestroy()
        {
            _destroyed = true;

            if (_ownsResolver)
            {
                try
                {
                    _resolver?.Dispose();
                }
                catch (AggregateException exception)
                {
                    ScopeHostEngine.LogFailures(exception, this);
                }
            }

            _resolver = null;
        }

        /// <summary>
        /// Override point for self-owned root scopes. Called by <see cref="Awake"/> when no external resolver
        /// has been supplied via <see cref="SetResolver"/>. Default is no-op so subclasses can opt in selectively.
        /// </summary>
        /// <param name="builder">The container builder; register services here as you would in a composition root.</param>
        protected virtual void Configure(IContainerBuilder builder) { }

        /// <summary>
        /// Supplies an externally built resolver, for example a child scope a coordinator created; call it at most
        /// once, before <c>Awake</c>.
        /// </summary>
        /// <param name="externalResolver">The resolver to host; must not be null.</param>
        /// <exception cref="InvalidOperationException">Thrown when a resolver is already set (by a prior <c>SetResolver</c> call or by <c>Awake</c>'s self-build path) or the host was destroyed.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="externalResolver"/> is null.</exception>
        public void SetResolver(IObjectResolver externalResolver)
        {
            if (_resolver != null || _destroyed)
                throw new InvalidOperationException(
                    "Resolver is already set or the host was destroyed; SetResolver may be called at most once, before Awake."
                );

            _resolver = externalResolver ?? throw new ArgumentNullException(nameof(externalResolver));
            _ownsResolver = false;
        }
    }
}