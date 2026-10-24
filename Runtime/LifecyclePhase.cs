namespace Didionysymus.Lattice.Runtime
{
    /// <summary>
    /// The lifecycle phase a scope was running when a participant failed, as reported by
    /// <see cref="Exceptions.LifecycleException"/>.
    /// </summary>
    public enum LifecyclePhase
    {
        /// <summary>
        /// The initialization pass or a supplied object's initialization: <see cref="IInitializable.Initialize"/> threw.
        /// </summary>
        Initialize,

        /// <summary>
        /// Startup: <see cref="IAsyncStartable.StartAsync"/> threw, returned a faulted task, or returned no task.
        /// </summary>
        Start
    }
}