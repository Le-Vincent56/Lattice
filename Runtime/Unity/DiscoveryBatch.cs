namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// Where a host's batch of roots came from, which decides whether a <see cref="ScopeBoundary"/> on a root is
    /// claimed and whether the roots are checked for overlap.
    /// </summary>
    internal enum DiscoveryBatch
    {
        /// <summary>
        /// The roots read from the host's scene; a boundary on a root is skipped.
        /// </summary>
        SceneRoots,

        /// <summary>
        /// Roots passed to the call; each claims a boundary on it, and no root may be given twice or beneath another.
        /// </summary>
        GivenRoots
    }
}