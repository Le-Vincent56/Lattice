namespace Didionysymus.Lattice.Runtime
{
    /// <summary>
    /// The lifecycle phase a scope was running when a participant failed, as reported by
    /// <see cref="Exceptions.LifecycleException"/>. Only the synchronous initialization phase exists today.
    /// </summary>
    public enum LifecyclePhase
    {
        Initialize
    }
}