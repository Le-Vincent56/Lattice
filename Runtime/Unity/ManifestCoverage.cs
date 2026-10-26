namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// The walk unit an <see cref="InjectionManifest"/> describes. The manifest stores it as an <c>int</c>, so the
    /// serialized value never depends on this enum's accessibility.
    /// </summary>
    internal enum ManifestCoverage
    {
        /// <summary>
        /// Never generated.
        /// </summary>
        None = 0,

        /// <summary>
        /// The scene batch of the host on whose GameObject the manifest sits.
        /// </summary>
        Scene = 1,

        /// <summary>
        /// The walk of the manifest's GameObject as a given root.
        /// </summary>
        Hierarchy = 2
    }
}