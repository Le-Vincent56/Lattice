namespace Didionysymus.Lattice.Runtime
{
    /// <summary>
    /// Represents a contract for types that require initialization logic before being used.
    /// Provides a method for executing any necessary setup or initialization prior to usage.
    /// </summary>
    public interface IInitializable
    {
        /// <summary>
        /// Executes initialization logic for the implementing type.
        /// </summary>
        void Initialize();
    }
}