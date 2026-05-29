using System;

namespace Didionysymus.Lattice.Runtime.Exceptions
{
    /// <summary>
    /// Thrown when a type has multiple public constructors and no clear injection point.
    /// </summary>
    public sealed class MultipleConstructorsException : DependencyResolutionException
    {
        public Type ImplType { get; }

        public MultipleConstructorsException(Type implType, int constructorCount)
            : base(
                $"Type '{implType.FullName}' has {constructorCount} public constructors. " +
                $"Annotate exactly one with [Inject], or reduce to a single public constructor."
            )
        {
            ImplType = implType;
        }
    }
}