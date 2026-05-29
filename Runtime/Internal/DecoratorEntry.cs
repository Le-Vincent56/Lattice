using System;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Represents a decorator registration that wraps a service type.
    /// Decorators are applied in registration order within the same scope.
    /// </summary>
    internal sealed class DecoratorEntry
    {
        public Type ServiceType { get; }
        public Type DecoratorType { get; }
        public Lifetime Lifetime { get; }

        public DecoratorEntry(
            Type serviceType, 
            Type decoratorType, 
            Lifetime lifetime
        )
        {
            ServiceType = serviceType;
            DecoratorType = decoratorType;
            Lifetime = lifetime;
        }
    }
}