namespace Didionysymus.Lattice.Runtime
{
    /// <summary>
    /// Defines a contract for configuring and registering dependencies into a container builder.
    /// </summary>
    public interface IInstaller
    {
        /// <summary>
        /// Adds registrations provided by the given installer to the container builder.
        /// </summary>
        /// <param name="builder">The container to install dependencies into.</param>
        void Install(IContainerBuilder builder);
    }
}