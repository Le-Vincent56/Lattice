namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Where a <see cref="Scope"/> is in its lifecycle. A scope starts <see cref="Created"/>, is
    /// <see cref="Initializing"/> while its initialization pass runs, becomes <see cref="Initialized"/> when the pass
    /// completes, and becomes <see cref="Faulted"/> when a pass throws; a faulted scope refuses further passes.
    /// </summary>
    internal enum LifecycleState
    {
        Created,
        Initializing,
        Initialized,
        Faulted
    }
}