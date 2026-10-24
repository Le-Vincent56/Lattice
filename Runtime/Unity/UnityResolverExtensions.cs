using System;
using UnityEngine;

namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// Unity-side conveniences over <see cref="IObjectResolver"/>:
    /// <list type="bullet">
    ///     <item>
    ///         <see cref="InjectGameObject"/> walks the GameObject's component tree (including inactive children)
    ///         and runs <c>Inject</c> on every <see cref="MonoBehaviour"/>
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
    /// must call one of these extensions if it needs <c>[Inject]</c> fields populated.
    /// </remarks>
    public static class UnityResolverExtensions
    {
        /// <summary>
        /// Walks <paramref name="root"/>'s entire component tree, including every child GameObject and inactive descendants
        /// and runs <see cref="IObjectResolver.Inject"/> on on every <see cref="MonoBehaviour"/> found. Missing
        /// script slots (where the script reference is broken) appear as null entries and are skipped.
        /// </summary>
        /// <param name="resolver">The resolver against which each MonoBehaviour's <c>[Inject]</c> members are bound.</param>
        /// <param name="root">The GameObject whose tree will be walked. Null is tolerated and silently no-ops so callers don't have to null-check before invocation.</param>
        /// <remarks>
        /// <c>includeInactive: true</c> is intentional: inactive components still need their dependencies bound in case they're activated later
        /// in the same scene's lifetime. The cost is negligible for typical scenes; large prefab trees with hundreds of inactive
        /// components may want a more targeted walk.
        /// </remarks>
        public static void InjectGameObject(this IObjectResolver resolver, GameObject root)
        {
            if (!root) return;

            MonoBehaviour[] components = root.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
            for (int i = 0; i < components.Length; i++)
            {
                MonoBehaviour component = components[i];

                // Missing-script slots return null; skip them
                if (!component) continue;

                resolver.Inject(component);
            }
        }

        /// <summary>
        /// Walks <paramref name="root"/>'s component tree, inactive descendants included, and initializes every
        /// <see cref="MonoBehaviour"/> that implements <see cref="IInitializable"/> once per scope through
        /// <see cref="ResolverLifecycleExtensions.InitializeInstance"/>. A component the scope already initialized, in
        /// an earlier call or because it is also a cached service, is skipped. Call it after
        /// <see cref="InjectGameObject"/> over the whole batch and after the scope's
        /// <see cref="ResolverLifecycleExtensions.RunInitializables"/>, so every <c>Initialize</c> body observes bound
        /// peers and initialized services.
        /// </summary>
        /// <param name="resolver">The resolver whose scope tracks which components it has initialized. Its scope must have completed <c>RunInitializables</c>.</param>
        /// <param name="root">The GameObject whose tree will be walked. Null is tolerated and silently no-ops.</param>
        /// <exception cref="InvalidOperationException">Thrown, when the walk reaches an initializable component, if the resolver is not the built-in resolver or its scope has not completed its initialization pass, is running one, or is faulted.</exception>
        /// <exception cref="Exceptions.LifecycleException">Thrown when a component's <c>Initialize</c> throws; the scope is then faulted.</exception>
        /// <remarks>
        /// Within one call, components are visited in <see cref="GameObject.GetComponentsInChildren{T}(bool)"/> order.
        /// That order is not a contract: a component must not depend on another component's <c>Initialize</c> having
        /// run.
        /// </remarks>
        public static void InitializeGameObject(this IObjectResolver resolver, GameObject root)
        {
            if (!root) return;

            MonoBehaviour[] components = root.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
            for (int i = 0; i < components.Length; i++)
            {
                MonoBehaviour component = components[i];
                if (!component) continue;
                if (component is not IInitializable) continue;
                
                resolver.InitializeInstance(component);
            }
        }

        /// <summary>
        /// Instantiates <paramref name="prefab"/> and immediately binds the resulting GameObject's component tree.
        /// Equivalent to <c>Object.Instantiate(prefab)</c> followed by <see cref="InjectGameObject"/> on the instance's
        /// GameObject, so spawned MonoBehaviours have populated <c>[Inject]</c> fields before their first <c>Start</c>.
        /// It binds only: no component's <see cref="IInitializable.Initialize"/> is called. To initialize the clone's
        /// components once, call <see cref="InitializeGameObject"/> on the instance after the scope's own pass.
        /// </summary>
        /// <param name="resolver">The resolver against which the spawned tree's <c>[Inject]</c> members are bound.</param>
        /// <param name="prefab">The prefab component to instantiate. Standard <c>Object.Instantiate</c> rules apply.</param>
        /// <typeparam name="T">The component type being instantiated; used so the caller receives a strongly-typed reference back.</typeparam>
        /// <returns>The instantiated component, with its own and every child MonoBehaviour's <c>[Inject]</c> members bound and nothing initialized.</returns>
        public static T InstantiateAndInject<T>(this IObjectResolver resolver, T prefab) where T : Component
        {
            T instance = UnityEngine.Object.Instantiate(prefab);
            resolver.InjectGameObject(instance.gameObject);
            return instance;
        }
    }
}