using System;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Represents a single service registration in the container's internal registry.
    /// Maps a service type to its implementation type, lifetime, and activation strategy. Every alias of one
    /// registration is its own entry and names the registration's first entry as its <see cref="Primary"/>.
    /// </summary>
    internal sealed class RegistrationEntry
    {
        public Type ServiceType { get; }
        public Type ImplType { get; }
        public Lifetime Lifetime { get; }
        public Func<IObjectResolver, object> Activator { get; set; }
        public object? Instance { get; }

        /// <summary>
        /// The first entry of the registration this entry belongs to, or this entry when it is the first. Scopes
        /// cache a Singleton or Scoped implementation instance under it, so every alias of one registration resolves
        /// that one instance.
        /// </summary>
        public RegistrationEntry Primary { get; }

        /// <summary>
        /// True when this registration was created via RegisterInstance (pre-built singleton).
        /// </summary>
        public bool IsPreBuiltInstance => Instance != null;

        public RegistrationEntry(
            Type serviceType,
            Type implType,
            Lifetime lifetime,
            Func<IObjectResolver, object> activator,
            object? instance = null,
            RegistrationEntry primary = null
        )
        {
            ServiceType = serviceType;
            ImplType = implType;
            Lifetime = lifetime;
            Activator = activator;
            Instance = instance;
            Primary = primary ?? this;
        }
    }
}