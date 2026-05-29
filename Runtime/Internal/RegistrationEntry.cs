using System;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Represents a single service registration in the container's internal registry.
    /// Maps a service type to its implementation type, lifetime, and activation strategy.
    /// </summary>
    internal sealed class RegistrationEntry
    {
        public Type ServiceType { get; }
        public Type ImplType { get; }
        public Lifetime Lifetime { get; }
        public Func<IObjectResolver, object> Activator { get; set; }
        public object? Instance { get; }

        /// <summary>
        /// True when this registration was created via RegisterInstance (pre-built singleton).
        /// </summary>
        public bool IsPreBuiltInstance => Instance != null;

        public RegistrationEntry(
            Type serviceType,
            Type implType,
            Lifetime lifetime,
            Func<IObjectResolver, object> activator,
            object? instance = null
        )
        {
            ServiceType = serviceType;
            ImplType = implType;
            Lifetime = lifetime;
            Activator = activator;
            Instance = instance;
        }
    }
}