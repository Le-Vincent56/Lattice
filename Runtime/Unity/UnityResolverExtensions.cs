using UnityEngine;
using Didionysymus.Lattice.Runtime;

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
        /// Walks <paramref name="root"/>'s component tree and invokes <see cref="IInitializable.Initialize"/>
        /// on every <see cref="MonoBehaviour"/> that implements it. Phase 2 of the strict two-phase contract
        /// for scene-attached objects; should be called AFTER <see cref="InjectGameObject"/> across every
        /// MonoBehaviour whose <c>Initialize</c> body might observe peer state, so peers' <c>[Inject]</c>
        /// fields are bound before any <c>Initialize</c> body runs.
        /// </summary>
        /// <param name="resolver">Unused at present; included for API symmetry with <see cref="InjectGameObject"/>. The Phase 2 walk does not require resolver state because every dependency is already bound from the Phase 1 pass.</param>
        /// <param name="root">The GameObject whose tree will be walked. Null is tolerated and silently no-ops.</param>
        /// <remarks>
        /// Within a single <c>InitializeGameObject</c> call, components are visited in
        /// <see cref="GameObject.GetComponentsInChildren{T}(bool)"/> order; that order is depth-first
        /// from <paramref name="root"/>. Cross-tree ordering is the caller's responsibility: when running
        /// Phase 2 across a whole scene, do all <see cref="InjectGameObject"/> calls first across every
        /// root, then all <c>InitializeGameObject</c> calls, as <see cref="LifetimeScopeBehaviour"/> does.
        /// </remarks>
        public static void InitializeGameObject(this IObjectResolver resolver, GameObject root)
        {
            if (!root) return;

            MonoBehaviour[] components = root.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
            for (int i = 0; i < components.Length; i++)
            {
                MonoBehaviour component = components[i];
                if (!component) continue;

                if (component is IInitializable initializable)
                {
                    initializable.Initialize();
                }
            }
        }

        /// <summary>
        /// Instantiates <paramref name="prefab"/> and immediately injects the resulting GameObject's component tree.
        /// Equivalent to <c>Object.Instantiate(prefab)</c> followed by <see cref="InjectGameObject"/> on the instance's GameObject;
        /// bundling the two ensures spawned MonoBehaviours have populated <c>[Inject]</c> fields before their first <c>Start</c> callback
        /// per the MonoBehaviour timing rules.
        /// </summary>
        /// <param name="resolver">The resolver against which the spawned tree's <c>[Inject]</c> members are bound.</param>
        /// <param name="prefab">The prefab component to instantiate. Standard <c>Object.Instantiate</c> rules apply.</param>
        /// <typeparam name="T">The component type being instantiated; used so the caller receives a strongly-typed reference back.</typeparam>
        /// <returns>The instantiated component, with its own and every child MonoBehaviour's <c>[Inject]</c> members bound.</returns>
        public static T InstantiateAndInject<T>(this IObjectResolver resolver, T prefab) where T : Component
        {
            T instance = Object.Instantiate(prefab);
            resolver.InjectGameObject(instance.gameObject);
            return instance;
        }
    }
}