namespace Didionysymus.Lattice.Tests.Editor.Fixtures
{
    // Open-generic test fixtures used by Tests/OpenGenerics tests.
    // IRepository<T> + Repository<T> exercise the simplest single-arity open-generic shape;
    // MonsterDefinition/AbilityDefinition are inert POCOs used as the closing type argument.
    // Both deliberately avoid constructor parameters so the tests pin open-generic resolution mechanics without
    // entangling lifecycle or dependency-graph concerns.

    /// <summary>
    /// Open-generic service interface. The container's open-generic registration cloess
    /// this against a concrete <c>T</c> on demand.
    /// </summary>
    public interface IRepository<T>
    {
        /// <summary>
        /// Returns the default value of <typeparamref name="T"/>.
        /// Present only so the closed implementation has at least one observable surface.
        /// </summary>
        T Sample { get; }
    }

    /// <summary>
    /// Default open-generic implementation of <see cref="IRepository{T}"/>. Holds no state;
    /// each closed instantiation has an independent <see cref="object.GetHashCode"/>
    /// identity for same/different-instance assertions.
    /// </summary>
    public sealed class Repository<T> : IRepository<T>
    {
        public T Sample { get; } = default;
    }

    /// <summary>
    /// Inert POCO used as a closing type argument. The name mirrors the project's monster-data
    /// domain, so test failures read clearly.
    /// </summary>
    public sealed class MonsterDefinition
    {
        public string Name { get; init; } = "Cinder Cub";
    }

    /// <summary>
    /// Inert POCO used as a second closing type argument, so multi-closure tests
    /// can show two distinct closed types resolving independently.
    /// </summary>
    public sealed class AbilityDefinition
    {
        public string Name { get; init; } = "Spark";
    }
}