using System;
using UnityEngine;
using Didionysymus.Lattice.Runtime;

namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// Scene-attached host for an <see cref="IObjectResolver"/>.
    /// Acts as the entry point that wires every MonoBehaviour in its scene with constructor-
    /// and member-injected dependencies before any of them reach their <c>Start</c> callback.
    /// <list type="bullet">
    ///     <item>
    ///         <c>[DefaultExecutionOrder(int.MinValue + 100)]</c> guarantees this <c>Awake</c> runs
    ///         before any user MonoBehaviour's <c>Awake</c>
    ///     </item>
    ///     <item>
    ///         Two construction modes: <see cref="Configure"/> override builds a self-owned root resolver, OR
    ///         <see cref="SetResolver"/> accepts an externally-built resolver (used by the Step-N <c>SceneCoordinator</c>
    ///         child-scope path)
    ///     </item>
    ///     <item>
    ///         In <c>Awake</c>: walks the scene's root GameObjects, calls <see cref="UnityResolverExtensions.InjectGameObject"/> on each,
    ///         then runs <see cref="ResolverLifecycleExtensions.RunInitializables"/> for Phase 2
    ///     </item>
    ///     <item>
    ///         In <c>OnDestroy</c>: disposes the resolver if and only if it was self-built; externally-provided resolver are
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
        /// Builds the resolver (if not already supplied externally) and walks the scene's root GameObjects to inject
        /// every MonoBehaviour beneath them, then runs Phase 2 across the resulting object graph.
        /// </summary>
        /// <remarks>
        /// Subclasses the override <c>Awake</c> MUST call <c>base.Awake()</c> first; otherwise the scope's
        /// scene-walk and Phase 2 runs are skipped, and dependent components will see null <c>[Inject]</c> fields
        /// in their own Awake/Start callbacks.
        /// </remarks>
        protected virtual void Awake()
        {
            if (_resolver == null)
            {
                // Self-build path: this behaviour is the composition root for the scene
                _resolver = Container.Build(Configure);
                _ownsResolver = true;
            }

            InjectScene();
            _resolver.RunInitializables();
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
        protected virtual void Configure(IContainerBuilder builder)
        {
        }

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
        /// Walks through the active scene's root GameObjects and runs <c>InjectGameObject</c> on each. Skips silently
        /// when this behaviour is not yet attached to a valid scene (e.g., during certain editor-only construction pathways
        /// that fire <c>Awake</c> before scene assignment completes).
        /// </summary>
        private void InjectScene()
        {
            UnityEngine.SceneManagement.Scene scene = gameObject.scene;
            if (!scene.IsValid()) return;

            GameObject[] roots = scene.GetRootGameObjects();

            // Phase 1: bind [Inject] members on every MonoBehaviour beneath every scene root
            for (int i = 0; i < roots.Length; i++)
            {
                _resolver.InjectGameObject(roots[i]);
            }

            // Phase 2: invoke Initialize on every IInitializable MonoBehaviour. Done as a separate pass
            // after Phase 1 completes across the entire scene, so cross-component peer sin Initialize
            // bodies always see populated [Inject] fields regardless of which scene root they live under.
            for (int i = 0; i < roots.Length; i++)
            {
                _resolver.InitializeGameObject(roots[i]);
            }
        }
    }
}