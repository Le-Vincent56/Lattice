namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// Where an <see cref="ExplicitScopeHost"/> is. A host starts <see cref="Detached"/>; <c>Attach</c> makes it
    /// <see cref="Attached"/>; <c>PrepareAsync</c> makes it <see cref="Preparing"/>, then <see cref="Ready"/> when every
    /// step succeeded, or <see cref="Faulted"/> when a step failed or was cancelled; <c>ShutdownAsync</c> or
    /// <c>OnDestroy</c> makes any attached host <see cref="Stopping"/>, and it returns to <see cref="Detached"/> once the
    /// prepare has settled and an owned resolver is shut down. A member may be added in a minor version; code that
    /// switches on it keeps a default branch.
    /// </summary>
    public enum ScopeHostState
    {
        /// <summary>
        /// No resolver; attach one.
        /// </summary>
        Detached,

        /// <summary>
        /// A resolver is attached; nothing in the scene is prepared.
        /// </summary>
        Attached,

        /// <summary>
        /// <c>PrepareAsync</c> is running.
        /// </summary>
        Preparing,

        /// <summary>
        /// The batch is bound and initialized and the scope's startup completed.
        /// </summary>
        Ready,

        /// <summary>
        /// A prepare step failed or was cancelled; shut the host down.
        /// </summary>
        Faulted,

        /// <summary>
        /// A shutdown is running.
        /// </summary>
        Stopping
    }
}