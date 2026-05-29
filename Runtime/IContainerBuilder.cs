using System;

namespace Didionysymus.Lattice.Runtime
{
    /// <summary>
    /// Represents a builder for configuring and constructing a dependency injection container.
    /// Provides methods for registering services, instances, factories, and decorators, as well
    /// as managing lifetime scopes and generic type registrations.
    /// </summary>
    public interface IContainerBuilder
    {
        /// <summary>
        /// Registers a service implementation type with the dependency injection container.
        /// </summary>
        /// <typeparam name="TImpl">The implementation type to register.</typeparam>
        /// <param name="lifetime">The lifetime of the registered service.</param>
        /// <returns>An instance of <see cref="IRegistration"/> for further configuration.</returns>
        IRegistration Register<TImpl>(Lifetime lifetime) where TImpl : class;

        /// <summary>
        /// Registers a service implementation type with the dependency injection container,
        /// associating it with a service interface type.
        /// </summary>
        /// <typeparam name="TService">The service type to register.</typeparam>
        /// <typeparam name="TImpl">The implementation type to associate with the service type.</typeparam>
        /// <param name="lifetime">The lifetime of the registered service.</param>
        /// <returns>An instance of <see cref="IRegistration"/> for further configuration.</returns>
        IRegistration Register<TService, TImpl>(Lifetime lifetime) where TImpl : class, TService;

        /// <summary>
        /// Registers an instance of a service with the dependency injection container.
        /// </summary>
        /// <typeparam name="TService">The service type to register.</typeparam>
        /// <param name="instance">The specific instance to associate with the service type.</param>
        /// <returns>An instance of <see cref="IRegistration"/> for further configuration.</returns>
        IRegistration RegisterInstance<TService>(TService instance);

        /// <summary>
        /// Registers a factory method for creating a service with the dependency injection container.
        /// </summary>
        /// <typeparam name="TService">The type of service to register.</typeparam>
        /// <param name="factory">The factory method used to create instances of the service.</param>
        /// <param name="lifetime">The lifetime of the registered service.</param>
        /// <returns>An instance of <see cref="IRegistration"/> for further configuration.</returns>
        IRegistration RegisterFactory<TService>(Func<IObjectResolver, TService> factory, Lifetime lifetime);

        /// <summary>
        /// Registers an open generic service implementation type with the dependency injection container,
        /// associating it with an open generic service type.
        /// </summary>
        /// <param name="openServiceType">The open generic service type to register.</param>
        /// <param name="openImplType">The open generic implementation type to associate with the service type.</param>
        /// <param name="lifetime">The lifetime of the registered open generic service.</param>
        /// <returns>An instance of <see cref="IRegistration"/> for further configuration.</returns>
        IRegistration RegisterOpenGeneric(Type openServiceType, Type openImplType, Lifetime lifetime);

        /// <summary>
        /// Ensures that specified closed generic service types are preserved in the dependency injection container.
        /// </summary>
        /// <param name="closedServiceTypes">An array of closed generic types to be preserved.</param>
        void PreserveClosedGenerics(params Type[] closedServiceTypes);

        /// <summary>
        /// Registers a decorator type for a specified service type in the dependency injection container.
        /// </summary>
        /// <typeparam name="TService">The service type to which the decorator is applied.</typeparam>
        /// <typeparam name="TDecorator">The decorator implementation type that wraps the service.</typeparam>
        /// <param name="lifetime">The lifetime of the decorator service.</param>
        void RegisterDecorator<TService, TDecorator>(Lifetime lifetime);

        /// <summary>
        /// Invokes the installation of services and configurations defined by the specified installer into the container builder.
        /// </summary>
        /// <param name="installer">An instance of <see cref="IInstaller"/> that defines the dependencies to be registered.</param>
        void Install(IInstaller installer);
    }
}