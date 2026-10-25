namespace Didionysymus.Lattice.Runtime
{
    /// <summary>
    /// Where a built-in scope is in its lifecycle. A scope starts <see cref="Created"/>, is
    /// <see cref="Initializing"/> while its initialization pass runs and <see cref="Initialized"/> once it completes,
    /// is <see cref="Starting"/> while its startup runs and <see cref="Ready"/> once it completes, and becomes
    /// <see cref="Faulted"/> when a pass or startup fails or startup is cancelled; a faulted scope refuses further
    /// lifecycle calls. <c>ShutdownAsync</c> moves any live scope to <see cref="Stopping"/>, and disposal moves any
    /// scope to <see cref="Disposed"/>. Read it with <see cref="ResolverLifecycleExtensions.GetLifecycleState"/>. A
    /// member may be added in a minor version; code that switches on it keeps a default branch.
    /// </summary>
    public enum LifecycleState
    {
        /// <summary>
        /// Built; no initialization pass has completed; the pass may run.
        /// </summary>
        Created,

        /// <summary>
        /// The initialization pass is running.
        /// </summary>
        Initializing,

        /// <summary>
        /// The initialization pass completed; startup has not begun; <c>InitializeInstance</c> and startup may run.
        /// </summary>
        Initialized,

        /// <summary>
        /// Startup is running; <c>InitializeInstance</c> runs, a second startup is refused.
        /// </summary>
        Starting,

        /// <summary>
        /// Startup completed; every participant started; <c>InitializeInstance</c> runs, startup starts nothing.
        /// </summary>
        Ready,

        /// <summary>
        /// A pass, a supplied object's initialization or startup failed, or startup was cancelled; every later pass,
        /// startup and <c>InitializeInstance</c> is refused; resolves and disposal still work. Terminal.
        /// </summary>
        Faulted,

        /// <summary>
        /// A shutdown is waiting for running startup work; resolves work, lifecycle calls are refused.
        /// </summary>
        Stopping,

        /// <summary>
        /// Disposal began. Terminal.
        /// </summary>
        Disposed
    }
}