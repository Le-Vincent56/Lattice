using System;

namespace Didionysymus.Lattice.Runtime.Exceptions
{
    /// <summary>
    /// Thrown by <see cref="IObjectResolver.CreateChildScope"/> when the child registers a service with
    /// <see cref="Lifetime.Singleton"/>. Singletons are cached and disposed by the root scope, so one registered
    /// in a child would be built from the child's registrations yet outlive the child; register it as
    /// <see cref="Lifetime.Scoped"/> in the child instead. Instances supplied through <c>RegisterInstance</c> are
    /// not affected, because the container neither builds nor disposes them.
    /// </summary>
    public sealed class ChildSingletonRegistrationException : DependencyResolutionException
    {
        /// <summary>
        /// The service type the child scope tried to register as a Singleton.
        /// </summary>
        public Type ServiceType { get; }

        /// <summary>
        /// The implementation type of the rejected registration.
        /// </summary>
        public Type ImplType { get; }

        public ChildSingletonRegistrationException(Type serviceType, Type implType)
            : base(BuildMessage(serviceType, implType))
        {
            ServiceType = serviceType;
            ImplType = implType;
        }

        /// <summary>
        /// Builds the message: names the rejected registration and the lifetime to use instead.
        /// </summary>
        /// <param name="serviceType">The service type of the rejected registration.</param>
        /// <param name="implType">The implementation type of the rejected registration.</param>
        /// <returns>A message naming both types and the corrective action.</returns>
        private static string BuildMessage(Type serviceType, Type implType)
        {
            return string.Format(
                "Child scopes cannot register Singletons: {0} ({1}) would be cached by the root and outlive the child. " +
                "Register it with Lifetime.Scoped for one instance per child, or register it in the root.",
                serviceType.FullName,
                implType.FullName
            );
        }
    }
}