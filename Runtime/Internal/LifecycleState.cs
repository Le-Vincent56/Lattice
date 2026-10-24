namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Where a <see cref="Scope"/> is in its lifecycle. A scope starts <see cref="Created"/>, is
    /// <see cref="Initializing"/> while its initialization pass runs and <see cref="Initialized"/> once it completes,
    /// is <see cref="Starting"/> while its startup runs and <see cref="Ready"/> once it completes, and becomes
    /// <see cref="Faulted"/> when a pass or startup fails or startup is cancelled; a faulted scope refuses further
    /// lifecycle calls.
    /// </summary>
    internal enum LifecycleState
    {
        /// <summary>
        /// Built; no initialization pass has completed.
        /// </summary>
        Created,

        /// <summary>
        /// The initialization pass is running.
        /// </summary>
        Initializing,

        /// <summary>
        /// The initialization pass completed; startup has not begun.
        /// </summary>
        Initialized,

        /// <summary>
        /// Startup is running.
        /// </summary>
        Starting,

        /// <summary>
        /// Startup completed; every participant started.
        /// </summary>
        Ready,

        /// <summary>
        /// A pass, a supplied object's initialization or startup failed, or startup was cancelled. Terminal.
        /// </summary>
        Faulted
    }
}