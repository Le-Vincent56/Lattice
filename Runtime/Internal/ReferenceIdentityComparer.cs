using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Compares objects by reference, ignoring any <see cref="object.Equals(object)"/> or
    /// <see cref="object.GetHashCode"/> override, so lifecycle bookkeeping treats two distinct instances as two
    /// participants even when their type defines value equality.
    /// </summary>
    internal sealed class ReferenceIdentityComparer : IEqualityComparer<object>
    {
        /// <summary>
        /// The shared instance; the comparer holds no state.
        /// </summary>
        public static readonly ReferenceIdentityComparer Instance = new ReferenceIdentityComparer();

        private ReferenceIdentityComparer()
        {
        }

        /// <summary>
        /// Returns whether <paramref name="x"/> and <paramref name="y"/> are the same object.
        /// </summary>
        /// <param name="x">The first object.</param>
        /// <param name="y">The second object.</param>
        /// <returns>True when both refer to the same instance, or both are null.</returns>
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);

        /// <summary>
        /// Returns the runtime's identity hash code for <paramref name="obj"/>, not its overridden hash code.
        /// </summary>
        /// <param name="obj">The object to hash.</param>
        /// <returns>The identity hash code.</returns>
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}