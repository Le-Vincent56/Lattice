using System;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Concrete <see cref="IContainerBuilder"/>. Accumulates registrations into
    /// a private <see cref="Registry"/> while user code calls <c>Register*</c> methods, then is consumed
    /// by <see cref="Container.Builder"/> which hands the registry off to a root <see cref="Scope"/>.
    /// One builder builds one container; not reusable across <see cref="Container.Build"/> calls.
    /// </summary>
    internal sealed class ContainerBuilder : IContainerBuilder
    {
        private readonly Registry _registry = new Registry();
        private readonly ExpressionActivatorFactory _activatorFactory = new ExpressionActivatorFactory();

        /// <summary>
        /// Internal accessor for <see cref="Container.Build"/> and <see cref="Scope.CreateChildScope"/>
        /// to extract the populated registry once user-supplied <c>configure</c> has run.
        /// </summary>
        internal Registry Registry => _registry;

        /// <summary>
        /// Registers a specific implementation type with the given lifetime in the container builder.
        /// </summary>
        /// <typeparam name="TImpl">The implementation type to be registered. Must be a reference type.</typeparam>
        /// <param name="lifetime">The lifetime of the registered implementation, determining its lifecycle within the container (e.g., Transient, Scoped, Singleton).</param>
        /// <returns>An object representing the registration configuration for the specified implementation type.</returns>
        public IRegistration Register<TImpl>(Lifetime lifetime) where TImpl : class
        {
            Type impl = typeof(TImpl);
            return new Registration(
                _registry,
                impl,
                lifetime,
                _activatorFactory.Build(impl),
                instance: null,
                initialServiceType: impl
            );
        }

        /// <summary>
        /// Registers a service of type <typeparamref name="TService"/> with an implementation of type <typeparamref name="TImpl"/> using the specified lifetime.
        /// </summary>
        /// <typeparam name="TService">The type of the service being registered.</typeparam>
        /// <typeparam name="TImpl">The implementation type of the service. Must implement <typeparamref name="TService"/> and be a reference type.</typeparam>
        /// <param name="lifetime">The lifetime configuration for the service, determining its lifecycle within the container (e.g., Transient, Scoped, Singleton).</param>
        /// <returns>An object representing the registration configuration for the given service and implementation type.</returns>
        public IRegistration Register<TService, TImpl>(Lifetime lifetime) where TImpl : class, TService
        {
            Type impl = typeof(TImpl);
            return new Registration(
                _registry,
                impl,
                lifetime,
                _activatorFactory.Build(impl),
                instance: null,
                initialServiceType: typeof(TService)
            );
        }

        /// <summary>
        /// Registers a specific instance of a service with a singleton lifetime in the container.
        /// </summary>
        /// <typeparam name="TService">The service type to be registered. Must be a reference type.</typeparam>
        /// <param name="instance">The instance of the service to be registered. Can be null, in which case the service type is used.</param>
        /// <returns>An object representing the registration configuration for the specified service instance.</returns>
        public IRegistration RegisterInstance<TService>(TService instance)
        {
            Type serviceType = typeof(TService);

            // Recover concrete type from the runtime instance when possible; gives a more
            // accurate ImplType for diagnostics. Falls back to TService when the instance is null.
            Type impl = instance != null
                ? instance.GetType()
                : serviceType;

            object Activator(IObjectResolver _) => instance;

            return new Registration(
                _registry,
                impl,
                Lifetime.Singleton,
                Activator,
                instance,
                initialServiceType: serviceType
            );
        }

        /// <summary>
        /// Registers a factory method for creating instances of the specified service type with the given lifetime in the container.
        /// </summary>
        /// <typeparam name="TService">The service type to be registered. Must be a reference type.</typeparam>
        /// <param name="factory">
        /// A factory method that takes an <see cref="IObjectResolver"/> and returns an instance of <typeparamref name="TService"/>.
        /// </param>
        /// <param name="lifetime">
        /// The lifetime of the registered service, determining its lifecycle within the container
        /// (e.g., Transient, Scoped, Singleton).
        /// </param>
        /// <returns>An object representing the registration configuration for the specified service type.</returns>
        public IRegistration RegisterFactory<TService>(Func<IObjectResolver, TService> factory, Lifetime lifetime)
        {
            Type serviceType = typeof(TService);

            // Wrap the typed factory in an object-returning delegate so it slots
            // into the RegistrationEntry.Activator signature without per-call boxing
            // of the delegate itself
            object Activator(IObjectResolver resolver) => factory.Invoke(resolver);

            return new Registration(
                _registry,
                serviceType,
                lifetime,
                Activator,
                instance: null,
                initialServiceType: serviceType
            );
        }

        /// <summary>
        /// Registers an open-generic implementation type against an open-generic service type.
        /// Closed-on-demand resolution promotes a closed <c>RegistrationEntry</c> on first <c>Resolve</c>
        /// of any compatible closed type. Both arguments must be open generic definitions (<see cref="Type.IsGenericTypeDefinition"/> == true),
        /// e.g. <c>typeof(IRepository&lt;&gt;)</c>.
        /// </summary>
        /// <param name="openServiceType">The open generic service-type definition.</param>
        /// <param name="openImplType">The open generic implementation-type definiiton.</param>
        /// <param name="lifetime">Lifetime applied to every closed instantiation produced from this registration.</param>
        /// <returns>An <see cref="IRegistration"/> whose <c>As</c>/<c>AsSelf</c>/<c>AsImplementedInterfaces</c> are keyed solely by their open service type.</returns>
        /// <exception cref="ArgumentNullException">Thrown when either type argument is null.</exception>
        /// <exception cref="ArgumentException">Thrown when either type is not an open generic definition.</exception>
        public IRegistration RegisterOpenGeneric(Type openServiceType, Type openImplType, Lifetime lifetime)
        {
            if (openServiceType == null) throw new ArgumentNullException(nameof(openServiceType));
            if (openImplType == null) throw new ArgumentNullException(nameof(openImplType));

            if (!openServiceType.IsGenericTypeDefinition)
            {
                throw new ArgumentException(
                    $"Type '{openServiceType.FullName}' is not an open generic definition.",
                    nameof(openServiceType)
                );
            }

            if (!openImplType.IsGenericTypeDefinition)
            {
                throw new ArgumentException(
                    $"Type '{openImplType.FullName}' is not an open generic definition.",
                    nameof(openImplType)
                );
            }

            OpenGenericEntry entry = new OpenGenericEntry(openServiceType, openImplType, lifetime);
            _registry.AddOpenGeneric(entry);

            // Open-generic registrations are keyed solely by their open service type. The pinning methods
            // (As / AsSelf / AsImplementedInterfaces) on a closed Registration are meaningless here,
            // so we hand back a no-op IRegistration to keep the fluent surface uniform.
            return new OpenGenericRegistration(openServiceType);
        }

        /// <summary>
        /// Marks specified closed generic service types to be preserved within the container, ensuring that they remain registered
        /// and resolvable even if no explicit dependencies refer to them.
        /// </summary>
        /// <param name="closedServiceTypes">An array of closed generic service types to preserve.</param>
        public void PreserveClosedGenerics(params Type[] closedServiceTypes)
        {
            for (int i = 0; i < closedServiceTypes.Length; i++)
            {
                _registry.PreservedClosedGenerics.Add(closedServiceTypes[i]);
            }
        }

        /// <summary>
        /// Registers a decorator type for a specified service type with the given lifetime in the container builder.
        /// </summary>
        /// <typeparam name="TService">The service type that the decorator will be applied to. Must be a reference type.</typeparam>
        /// <typeparam name="TDecorator">The decorator type to be applied to the specified service type. Must implement or derive from <typeparamref name="TService"/>.</typeparam>
        /// <param name="lifetime">The lifetime of the decorator, determining its lifecycle within the container (e.g., Transient, Scoped, Singleton).</param>
        public void RegisterDecorator<TService, TDecorator>(Lifetime lifetime)
        {
            DecoratorEntry entry = new DecoratorEntry(typeof(TService), typeof(TDecorator), lifetime);
            _registry.AddDecorator(entry);
        }

        /// <summary>
        /// Invokes the specified installer to register dependencies into the container builder.
        /// </summary>
        /// <param name="installer">The installer to be executed, which configures and registers dependencies into the container builder.</param>
        public void Install(IInstaller installer) => installer.Install(this);

        /// <summary>
        /// No-op <see cref="IRegistration"/> returned by <see cref="RegisterOpenGeneric"/>.
        /// The pinning methods are kept solely to satisfy the fluent API; open-generic registrations
        /// cannot be rebound to additional service types.
        /// </summary>
        private sealed class OpenGenericRegistration : IRegistration
        {
            private readonly Type _openType;

            public OpenGenericRegistration(Type openType) => _openType = openType;

            public IRegistration As<TService>() => this;
            public IRegistration AsSelf() => this;
            public IRegistration AsImplementedInterfaces() => this;
        }
    }
}