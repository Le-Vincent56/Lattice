namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// Who disposes the resolver an <see cref="ExplicitScopeHost"/> is attached to. The default value is
    /// <see cref="Borrowed"/>, which never disposes.
    /// </summary>
    public enum ScopeOwnership
    {
        /// <summary>
        /// The attacher owns the resolver; the host never disposes it.
        /// </summary>
        Borrowed,

        /// <summary>
        /// The host owns the resolver and shuts it down when the host shuts down or is destroyed.
        /// </summary>
        Owned
    }
}