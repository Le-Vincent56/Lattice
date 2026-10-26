using System.Collections.Generic;
using UnityEngine;

namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// One batch's two ordered role lists: the components to bind and the components to initialize. Filled by a walk or
    /// from an <see cref="InjectionManifest"/>; rented from and returned to <see cref="ComponentDiscovery"/>'s
    /// main-thread pool, so a holder never keeps it past the call that rented it.
    /// </summary>
    internal sealed class InjectionTargets
    {
        /// <summary>
        /// The components to pass to <c>Inject</c>, in walk order.
        /// </summary>
        public List<MonoBehaviour> Inject { get; } = new List<MonoBehaviour>();

        /// <summary>
        /// The <see cref="IInitializable"/> components to initialize after the scope's pass, in walk order.
        /// </summary>
        public List<MonoBehaviour> Initialize { get; } = new List<MonoBehaviour>();

        /// <summary>
        /// Empties both lists, so the targets keep no scene reference.
        /// </summary>
        public void Clear()
        {
            Inject.Clear();
            Initialize.Clear();
        }
    }
}