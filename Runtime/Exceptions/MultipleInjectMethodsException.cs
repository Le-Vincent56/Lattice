using System;

namespace Didionysymus.Lattice.Runtime.Exceptions
{
    /// <summary>
    /// Thrown when a type has more than one method annotated with <see cref="InjectAttribute"/>.
    /// At most one <c>[Inject]</c> method per type is allowed; if a derived class declares one,
    /// it shadows any base-class <c>[Inject]</c> methods (which are then ignored).
    /// </summary>
    public sealed class MultipleInjectMethodsException : DependencyResolutionException
    {
        public Type ImplType { get; }

        public MultipleInjectMethodsException(Type implType, int injectMethodCount) : base(
            $"Type '{implType.FullName}' has {injectMethodCount} methods annotated with [Inject]." +
            "At most one [Inject] method per type is allowed."
        )
        {
            ImplType = implType;
        }
    }
}