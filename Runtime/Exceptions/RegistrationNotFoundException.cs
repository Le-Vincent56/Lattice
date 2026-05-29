using System;

namespace Didionysymus.Lattice.Runtime.Exceptions
{
    /// <summary>
    /// Thrown when a registration cannot be found.
    /// </summary>
    public sealed class RegistrationNotFoundException : DependencyResolutionException
    {
        public Type RequestedType { get; }

        public RegistrationNotFoundException(Type requestedType) 
            : base(
                $"No registration for service type '{requestedType.FullName}' in this scope or any ancestor"
            )
        {
            RequestedType = requestedType;
        }
    
        public RegistrationNotFoundException(Type requestedType, string requestChain) 
            : base(
                $"No registration for service type '{requestedType.FullName}' in this scope or any ancestor.\n Request chain: {requestChain}" 
            )
        {
            RequestedType = requestedType;
        }
    }
}