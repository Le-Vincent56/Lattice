namespace Didionysymus.Lattice.Runtime
{
    /// <summary>
    /// Represents a registration entry in a dependency injection container,
    /// allowing further configuration of the registered service.
    /// </summary>
    public interface IRegistration
    {
        /// <summary>
        /// Configures the registration to expose the registered service
        /// as the specified type for resolution.
        /// </summary>
        /// <typeparam name="TService">The type to expose for resolution.</typeparam>
        /// <returns>The current registration instance for chaining further configurations.</returns>
        IRegistration As<TService>();

        /// <summary>
        /// Configures the registration to expose the registered service
        /// as itself for resolution.
        /// </summary>
        /// <returns>The current registration instance for chaining further configurations.</returns>
        IRegistration AsSelf();

        /// <summary>
        /// Configures the registration to expose the registered service as all of the interfaces
        /// it implements for resolution.
        /// </summary>
        /// <returns>The current registration instance for chaining further configurations.</returns>
        IRegistration AsImplementedInterfaces();
    }
}