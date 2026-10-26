using System;
using System.Collections.Generic;
using UnityEngine;

namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// Unity-side conveniences over <see cref="IObjectResolver"/>, sharing the hosts' walk and its rules: one
    /// <c>GetComponentsInChildren</c> walk per call over the root's tree, inactive objects included; only
    /// <see cref="MonoBehaviour"/>s, null (missing-script) and destroyed entries skipped; a <see cref="ScopeBoundary"/>
    /// subtree beneath the root skipped, while the root claims a boundary on itself; no host check. A root carrying a
    /// usable hierarchy <see cref="InjectionManifest"/> is read from it instead of walked; an unusable one is refused
    /// with <see cref="InjectionManifestException"/> before anything is bound. Main thread only.
    /// <list type="bullet">
    ///     <item>
    ///         <see cref="InjectGameObject"/> runs <c>Inject</c> on every component of the walk whose type has
    ///         injection members
    ///     </item>
    ///     <item>
    ///         <see cref="InitializeGameObject"/> initializes every <see cref="IInitializable"/> component of the walk
    ///         once per scope
    ///     </item>
    ///     <item>
    ///         <see cref="InstantiateAndInject{T}"/> spawns a prefab and immediately injects the resulting GameObject
    ///         so its MonoBehaviours have populated <c>[Inject]</c> fields before <c>Start</c> runs
    ///     </item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// These extensions live in <c>Didionysymus.Lattice.Unity</c> (engine-dependent), keeping <c>Didionysymus.Lattice</c> itself
    /// engine-free per the asmdef boundary. Member injection still requires an explicit call: there is no auto-Inject during
    /// constructor activation, so any code path that creates MonoBehaviours outside of <see cref="LifetimeScopeBehaviour"/>'s scene-walk
    /// must call one of these extensions if it needs <c>[Inject]</c> fields populated. A nested call (an <c>[Inject]</c>
    /// method or an <c>Initialize</c> that calls an extension on another root) walks with its own buffer.
    /// </remarks>
    public static class UnityResolverExtensions
    {
        /// <summary>
        /// Walks <paramref name="root"/>'s entire component tree once, including every child GameObject and inactive
        /// descendants, and runs <see cref="IObjectResolver.Inject"/> on every <see cref="MonoBehaviour"/> whose type
        /// has an <c>[Inject]</c> field, writable property or method, inherited ones included; a component without one
        /// is never passed to <c>Inject</c>. A <see cref="ScopeBoundary"/> subtree beneath the root is skipped; a
        /// boundary on the root itself is claimed. Missing script slots appear as null entries and are skipped. When
        /// <paramref name="root"/> carries a valid hierarchy <see cref="InjectionManifest"/>, exactly its inject list is
        /// bound, in order, and nothing is walked; a scene manifest of a readable format is ignored.
        /// </summary>
        /// <param name="resolver">The resolver against which each MonoBehaviour's <c>[Inject]</c> members are bound.</param>
        /// <param name="root">The GameObject whose tree will be walked. Null is tolerated and silently no-ops so callers don't have to null-check before invocation.</param>
        /// <exception cref="InvalidOperationException">Thrown when called off Unity's main thread, before anything is touched, a null <paramref name="root"/> included.</exception>
        /// <exception cref="InjectionManifestException">Thrown before anything is bound when the <see cref="InjectionManifest"/> on <paramref name="root"/> cannot be used (never generated, an unreadable format, or a listed component that is missing, has lost its role, has left the root or sits inside a boundary) and the development fallback does not apply.</exception>
        /// <remarks>
        /// <c>includeInactive: true</c> is intentional: inactive components still need their dependencies bound in case they're activated later
        /// in the same scene's lifetime. Components are visited in <c>GetComponentsInChildren</c> order, which is not a
        /// contract.
        /// </remarks>
        public static void InjectGameObject(this IObjectResolver resolver, GameObject root)
        {
            ScopeHostEngine.ThrowIfNotMainThread(nameof(UnityResolverExtensions) + "." + nameof(InjectGameObject));

            if (!root) return;

            InjectionTargets targets = ComponentDiscovery.Rent();
            try
            {
                ComponentDiscovery.CollectRoot(root, targets);
                List<MonoBehaviour> inject = targets.Inject;
                for (int i = 0; i < inject.Count; i++)
                {
                    MonoBehaviour component = inject[i];
                    if (component) resolver.Inject(component);
                }
            }
            finally
            {
                ComponentDiscovery.Return(targets);
            }
        }

        /// <summary>
        /// Walks <paramref name="root"/>'s component tree once, inactive descendants included and
        /// <see cref="ScopeBoundary"/> subtrees beneath the root skipped, and initializes every
        /// <see cref="MonoBehaviour"/> that implements <see cref="IInitializable"/> once per scope through
        /// <see cref="ResolverLifecycleExtensions.InitializeInstance"/>. A component the scope already initialized, in
        /// an earlier call or because it is also a cached service, is skipped. When <paramref name="root"/> carries a
        /// valid hierarchy <see cref="InjectionManifest"/>, exactly its initialize list is used, in order, and nothing
        /// is walked. Call it after <see cref="InjectGameObject"/> over the whole batch and after the scope's
        /// <see cref="ResolverLifecycleExtensions.RunInitializables"/>, so every <c>Initialize</c> body observes bound
        /// peers and initialized services.
        /// </summary>
        /// <param name="resolver">The resolver whose scope tracks which components it has initialized. Its scope must have completed <c>RunInitializables</c>.</param>
        /// <param name="root">The GameObject whose tree will be walked. Null is tolerated and silently no-ops.</param>
        /// <exception cref="InvalidOperationException">Thrown when called off Unity's main thread, before anything is touched, a null <paramref name="root"/> included; and, when the walk reaches an initializable component, if the resolver is not the built-in resolver or its scope has not completed its initialization pass, is running one, or is faulted.</exception>
        /// <exception cref="InjectionManifestException">Thrown before anything is initialized when the <see cref="InjectionManifest"/> on <paramref name="root"/> cannot be used and the development fallback does not apply.</exception>
        /// <exception cref="Exceptions.LifecycleException">Thrown when a component's <c>Initialize</c> throws; the scope is then faulted.</exception>
        /// <remarks>
        /// Within one call, components are visited in <see cref="GameObject.GetComponentsInChildren{T}(bool)"/> order.
        /// That order is not a contract: a component must not depend on another component's <c>Initialize</c> having
        /// run.
        /// </remarks>
        public static void InitializeGameObject(this IObjectResolver resolver, GameObject root)
        {
            ScopeHostEngine.ThrowIfNotMainThread(nameof(UnityResolverExtensions) + "." + nameof(InitializeGameObject));

            if (!root) return;

            InjectionTargets targets = ComponentDiscovery.Rent();
            try
            {
                ComponentDiscovery.CollectRoot(root, targets);
                List<MonoBehaviour> initialize = targets.Initialize;
                for (int i = 0; i < initialize.Count; i++)
                {
                    MonoBehaviour component = initialize[i];
                    if (component) resolver.InitializeInstance(component);
                }
            }
            finally
            {
                ComponentDiscovery.Return(targets);
            }
        }

        /// <summary>
        /// Instantiates <paramref name="prefab"/> and immediately binds the resulting GameObject's component tree.
        /// Equivalent to <c>Object.Instantiate(prefab)</c> followed by <see cref="InjectGameObject"/> on the instance's
        /// GameObject, so spawned MonoBehaviours have populated <c>[Inject]</c> fields before their first <c>Start</c>.
        /// A hierarchy <see cref="InjectionManifest"/> on the prefab's root is copied with references pointed at the
        /// clone, so the clone is bound from its own manifest and the prefab is never touched. It binds only: no
        /// component's <see cref="IInitializable.Initialize"/> is called. To initialize the clone's components once,
        /// call <see cref="InitializeGameObject"/> on the instance after the scope's own pass.
        /// </summary>
        /// <param name="resolver">The resolver against which the spawned tree's <c>[Inject]</c> members are bound.</param>
        /// <param name="prefab">The prefab component to instantiate. Standard <c>Object.Instantiate</c> rules apply.</param>
        /// <typeparam name="T">The component type being instantiated; used so the caller receives a strongly-typed reference back.</typeparam>
        /// <returns>The instantiated component, with its own and every child MonoBehaviour's <c>[Inject]</c> members bound and nothing initialized.</returns>
        /// <exception cref="InjectionManifestException">Thrown before anything is bound when the clone's manifest cannot be used and the development fallback does not apply; the clone stays in the scene, unreturned, and Lattice destroys nothing.</exception>
        public static T InstantiateAndInject<T>(this IObjectResolver resolver, T prefab) where T : Component
        {
            T instance = UnityEngine.Object.Instantiate(prefab);
            resolver.InjectGameObject(instance.gameObject);
            return instance;
        }
    }
}