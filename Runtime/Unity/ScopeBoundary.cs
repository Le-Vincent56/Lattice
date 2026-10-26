using UnityEngine;

namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// Marks this GameObject's subtree as prepared by another scope. Every walk skips it and its descendants unless the
    /// GameObject is a root passed to the call; a scene host also skips one on a scene root. Binds, initializes and
    /// destroys nothing; a disabled marker, or one on an inactive GameObject, still counts.
    /// </summary>
    /// <remarks>
    /// Prepare the subtree with the scope that owns it: an <see cref="ExplicitScopeHost"/> given the subtree's root,
    /// or <see cref="UnityResolverExtensions.InjectGameObject"/> then
    /// <see cref="UnityResolverExtensions.InitializeGameObject"/> with that scope's resolver. A walk that is given the
    /// subtree's root still skips the boundaries beneath it. A <see cref="LifetimeScopeBehaviour"/> cannot sit inside
    /// a boundary.
    /// </remarks>
    public sealed class ScopeBoundary : MonoBehaviour { }
}