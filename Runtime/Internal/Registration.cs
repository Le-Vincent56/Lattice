using System;
using System.Collections.Generic;
using Didionysymus.Lattice.Runtime.Exceptions;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Concrete <see cref="IRegistration"/> returned to user code by <see cref="ContainerBuilder"/>.
    /// On construction, registers itself in the <see cref="Registry"/> under the initial service type.
    /// Subsequent fluent calls (<c>As&lt;T&gt;()</c>, <c>AsSelf()</c>, <c>AsImplementedInterfaces()</c>)
    /// add additional service-type aliases that all share the same activator, lifetime, and instance;
    /// so resolving any of the bound service types yields the same Singleton/Scoped instance.
    /// </summary>
    internal sealed class Registration : IRegistration
    {
        private readonly Registry _registry;
        private readonly RegistrationEntry _entry;
        private readonly Type _implType;
        private readonly Lifetime _lifetime;
        private readonly Func<IObjectResolver, object> _activator;
        private readonly object _instance;
        private readonly List<Type> _serviceTypes = new List<Type>();

        public Registration(
            Registry registry,
            Type implType,
            Lifetime lifetime,
            Func<IObjectResolver, object> activator,
            object instance,
            Type initialServiceType
        )
        {
            _registry = registry;
            _implType = implType;
            _lifetime = lifetime;
            _activator = activator;
            _instance = instance;
            _entry = new RegistrationEntry(
                initialServiceType,
                implType,
                lifetime,
                activator,
                instance
            );
            _serviceTypes.Add(initialServiceType);
            _registry.Add(_entry);
        }

        /// <summary>
        /// Adds an additional service-type binding. All bindings share the same activator and lifetime;
        /// resolving any of them returns the same instance for Singleton/Scoped lifetimes.
        /// </summary>
        /// <typeparam name="TService">The service type to bind to the current implementation type.</typeparam>
        /// <returns>The current registration instance updated with the new service type mapping.</returns>
        /// <exception cref="DependencyResolutionException">Thrown if the implementation type does not implement or inherit from the specified service type.</exception>
        public IRegistration As<TService>()
        {
            Type serviceType = typeof(TService);

            if (!serviceType.IsAssignableFrom(_implType))
                throw new DependencyResolutionException(
                    $"Type '{_implType.FullName}' does not implement '{serviceType.FullName}'");

            return As(serviceType);
        }

        /// <summary>
        /// Adds the implementation type itself as a service-type binding.
        /// Useful when registering via <c>Register&lt;TService, TImpl&gt;()</c> and also
        /// wanting <c>resolver.Resolve&lt;TImpl&gt;()</c> to succeed.
        /// </summary>
        /// <returns>The current registration instance updated with the implementation type mapping.</returns>
        public IRegistration AsSelf() => As(_implType);

        /// <summary>
        /// Adds bindings for every interface directly or indirectly implemented by the implementation type.
        /// Equivalent to calling <c>As&lt;I&gt;()</c> for each <c>I</c> in <c>_implType.GetInterfaces()</c>.
        /// </summary>
        /// <returns>The current registration instance for chaining further configurations.</returns>
        public IRegistration AsImplementedInterfaces()
        {
            Type[] interfaces = _implType.GetInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                As(interfaces[i]);
            }

            return this;
        }

        /// <summary>
        /// Internal helper that registers a new <see cref="RegistrationEntry"/> for the
        /// given service type, sharing the same activator and instance as the original entry. Skips duplicates
        /// so the fluent API stays idempotent.
        /// </summary>
        /// <typeparam name="TService">The type of the service to register.</typeparam>
        /// <returns>The current registration instance updated with the specified service type mapping.</returns>
        /// <exception cref="DependencyResolutionException">Thrown when the implementation type does not implement or inherit from the specified service type.</exception>
        private IRegistration As(Type serviceType)
        {
            if (_serviceTypes.Contains(serviceType)) return this;

            _serviceTypes.Add(serviceType);
            _registry.Add(new RegistrationEntry(
                serviceType,
                _implType,
                _lifetime,
                _activator,
                _instance
            ));
            return this;
        }
    }
}