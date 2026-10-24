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
    ///         Two construction modes: <see cref="Configure"/> override builds a self-owned root resolver, OR
    ///         <see cref="SetResolver"/> accepts an externally-built resolver, for example a child scope built by a
    ///         coordinator that has already run the parent's initialization pass
    ///     </item>
    ///     <item>
    ///         In <c>Awake</c>: walks the scene's root GameObjects and calls <see cref="UnityResolverExtensions.InjectGameObject"/>
    ///         on each, runs <see cref="ResolverLifecycleExtensions.RunInitializables"/> for the scope's own services,
    ///         then calls <see cref="UnityResolverExtensions.InitializeGameObject"/> on each root so every
    ///         <see cref="IInitializable"/> component is initialized once, after the services it was bound to
    ///     </item>
    ///     <item>
    ///         In <c>OnDestroy</c>: disposes the resolver if and only if it was self-built; externally-provided resolvers are
    ///         owned by their builder
    ///     </item>
    /// </list>
    /// </summary>
    [DefaultExecutionOrder(int.MinValue + 100)]
    public class LifetimeScopeBehaviour : MonoBehaviour
    {
        private IObjectResolver _resolver;

        // Tre when this behaviour built its own resolver via Container.Build(Configure); false when an external
        // resolver was supplied via SetResolver. Drives whether OnDestroy disposes the resolver; externally-owned resolvers
        // are disposed by their builder, not by this behaviour.
        private bool _ownsResolver;

        /// <summary>
        /// The active resolver. Available after <c>Awake</c> runs. Null before; consumers that need to inject
        /// late-spawned GameObjects should use <see cref="UnityResolverExtensions.InjectGameObject"/> against this.
        /// </summary>
        public IObjectResolver Resolver => _resolver;

        /// <summary>
        /// Builds the resolver (if not already supplied externally), fixes the batch as the scene's root GameObjects
        /// at that moment, binds every MonoBehaviour beneath them, runs the scope's initialization pass, then
        /// initializes the batch's <see cref="IInitializable"/> components once each. An object created during the
        /// pass is in neither walk; bind and initialize it explicitly with <c>InjectGameObject</c> and
        /// <c>InitializeGameObject</c>.
        /// </summary>
        /// <remarks>
        /// Subclasses that override <c>Awake</c> MUST call <c>base.Awake()</c> first; otherwise the scene walk and
        /// both initialization passes are skipped, and dependent components will see null <c>[Inject]</c> fields
        /// in their own Awake/Start callbacks. If the service pass throws, <c>Awake</c> throws before any component
        /// is initialized; a self-built resolver is still disposed in <c>OnDestroy</c>.
        /// </remarks>
        protected virtual void Awake()
        {
            if (_resolver == null)
            {
                // Self-build path: this behaviour is the composition root for the scene
                _resolver = Container.Build(Configure);
                _ownsResolver = true;
            }

            // One array for both walks, so nothing is initialized that was not bound.
            GameObject[] roots = GetSceneRoots();
            InjectScene(roots);
            _resolver.RunInitializables();
            InitializeScene(roots);
        }
        
        /// <summary>
        /// Disposes the resolver only if this behaviour built it.
        /// Externally-provided resolvers are owned and disposed by whoever called
        /// <see cref="SetResolver"/>.
        /// </summary>
        protected virtual void OnDestroy()
        {
            if (!_ownsResolver) return;

            _resolver?.Dispose();
        }

        /// <summary>
        /// Override point for self-owned root scopes. Called by <see cref="Awake"/> when no external resolver
        /// has been supplied via <see cref="SetResolver"/>. Default is no-op so subclasses can opt in selectively.
        /// </summary>
        /// <param name="builder">The container builder; register services here as you would in a composition root.</param>
        protected virtual void Configure(IContainerBuilder builder) { }

        /// <summary>
        /// Provides an externally-built resolver; used by the SceneCoordinator child-scope path where the parent is a different
        /// scope's resolver and this behaviour merely receives the child built from it. May be called at most once, before <c>Awake</c> calling
        /// after <c>Awake</c> or twice throws.
        /// </summary>
        /// <param name="externalResolver">The resolver to host; must not be null.</param>
        /// <exception cref="InvalidOperationException">Thrown when a resolver is already set (either by a prior <c>SetResolver</c> call or by <c>Awake</c>'s self-build path.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="externalResolver"/> is null.</exception>
        public void SetResolver(IObjectResolver externalResolver)
        {
            if (_resolver != null)
                throw new InvalidOperationException(
                    "Resolver is already set. SetResolver may be called at most once before Awake.");

            _resolver = externalResolver ?? throw new ArgumentNullException(nameof(externalResolver));
            _ownsResolver = false;
        }

        /// <summary>
        /// Returns the root GameObjects of the scene this behaviour lives in, or an empty array when it is not yet
        /// attached to a valid scene (e.g., during certain editor-only construction pathways that fire <c>Awake</c>
        /// before scene assignment completes), so both walks silently do nothing.
        /// </summary>
        /// <returns>The scene's root GameObjects at the moment of the call.</returns>
        private GameObject[] GetSceneRoots()
        {
            UnityEngine.SceneManagement.Scene scene = gameObject.scene;
            return scene.IsValid() ? scene.GetRootGameObjects() : Array.Empty<GameObject>();
        }

        /// <summary>
        /// Runs <c>InjectGameObject</c> on each of <paramref name="roots"/>, so every MonoBehaviour's <c>[Inject]</c>
        /// members in the batch are bound before any <c>Initialize</c> body runs.
        /// </summary>
        /// <param name="roots">The batch: the scene's root GameObjects fixed in <see cref="Awake"/>.</param>
        private void InjectScene(GameObject[] roots)
        {
            for (int i = 0; i < roots.Length; i++)
            {
                _resolver.InjectGameObject(roots[i]);
            }
        }

        /// <summary>
        /// Runs <c>InitializeGameObject</c> on each of <paramref name="roots"/>, after the scope's own initialization
        /// pass, so a component's <c>Initialize</c> observes bound peers and initialized services and runs once per
        /// scope.
        /// </summary>
        /// <param name="roots">The same batch <see cref="InjectScene"/> bound.</param>
        private void InitializeScene(GameObject[] roots)
        {
            for (int i = 0; i < roots.Length; i++)
            {
                _resolver.InitializeGameObject(roots[i]);
            }
        }
    }
}