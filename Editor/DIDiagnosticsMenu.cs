using Didionysymus.Lattice.Runtime;
using UnityEditor;
using UnityEngine;
using Didionysymus.Lattice.Runtime.Unity;

namespace Didionysymus.Lattice.Editor
{
    /// <summary>
    /// Hosts the <c>Lattice/Dump Active Scope Tree</c> Unity Editor menu item.
    /// In Play mode, locates the active <see cref="LifetimeScopeBehaviour"/> with a live
    /// <see cref="LifetimeScopeBehaviour.Resolver"/> and writes its
    /// <see cref="DiagnosticsExtensions.DumpScopeTree"/> output to the console.
    /// In Edit mode, the item logs a warning and no-ops, since no live container exists.
    /// </summary>
    public static class DIDiagnosticsMenu
    {
        /// <summary>
        /// Menu handler for <c>Lattice/Dump Active SCope Tree</c>. Walks the loaded scene for the first
        /// <see cref="LifetimeScopeBehaviour"/> whose resolver has been wired up (either via self-build in
        /// <c>Awake</c> or via an external <see cref="LifetimeScopeBehaviour.SetResolver"/> call) and logs the dump string.
        /// Multi-root scenes are unusual; the first hit wins.
        /// </summary>
        [MenuItem("Lattice/Dump Active Scope Tree", priority = 100)]
        private static void DumpActiveScopeTree()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning(
                    "[Lattice] Dump Active Scope Tree requires Play Mode (no live container in Edit mode)."
                );
                return;
            }

            LifetimeScopeBehaviour[] lifetimeScopes = Object.FindObjectsByType<LifetimeScopeBehaviour>();
            if (lifetimeScopes == null || lifetimeScopes.Length == 0)
            {
                Debug.LogWarning("[Lattice] No LifetimeScopeBehaviour found in the active scene.");
                return;
            }

            LifetimeScopeBehaviour rootBehaviour = null;
            for (int i = 0; i < lifetimeScopes.Length; i++)
            {
                LifetimeScopeBehaviour behaviour = lifetimeScopes[i];

                // Implicit-bool null check onf the MonoBehaviour catches Unity's
                // "fake null" (managed reference still alive but native object destroyed).
                // Resolver is a plain managed interface so the explicit != null check is the right
                // idiom there.
                if (!behaviour) continue;
                if (behaviour.Resolver == null) continue;

                rootBehaviour = behaviour;
                break;
            }

            if (!rootBehaviour)
            {
                Debug.LogWarning("[Lattice] Found LifetimScopeBehaviour(s) but none have an active resolver.");
                return;
            }

            string dump = rootBehaviour.Resolver.DumpScopeTree();
            Debug.Log("[Lattice] Active Scope Tree:\n" + dump);
        }
    }
}