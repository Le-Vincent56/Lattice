using System;
using System.Collections.Generic;

namespace Didionysymus.Lattice.Runtime
{
    /// <summary>
    /// Resolves objects, manages child scopes, and dependency injection within a container.
    /// </summary>
    public interface IObjectResolver : IDisposable
    {
        /// <summary>
        /// Resolves an instance of the specified type T from the dependency injection container.
        /// </summary>
        /// <typeparam name="T">The type of the object to resolve.</typeparam>
        /// <returns>An instance of the specified type T resolved from the container.</returns>
        T Resolve<T>();

        /// <summary>
        /// Resolves an instance of the specified type from the dependency injection container.
        /// </summary>
        /// <param name="type">The type of the object to resolve.</param>
        /// <returns>An instance of the specified type resolved from the container.</returns>
        object Resolve(Type type);

        /// <summary>
        /// Resolves all instances of the specified type T from the dependency injection container.
        /// </summary>
        /// <typeparam name="T">The type of the objects to resolve.</typeparam>
        /// <returns>A read-only list of instances of the specified type T resolved from the container.</returns>
        IReadOnlyList<T> ResolveAll<T>();

        /// <summary>
        /// Creates a child scope. The child inherits this scope's registrations, and its own registrations shadow
        /// them for resolves that start in the child. A child may register Scoped and Transient services and
        /// supply instances; it may not register Singletons, which belong to the root.
        /// </summary>
        /// <param name="configure">Registers the child's services on a fresh <see cref="IContainerBuilder"/>.</param>
        /// <returns>The new child scope. Dispose it to release what it owns; disposing this scope disposes it too.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when this scope has been disposed.</exception>
        /// <exception cref="Exceptions.ChildSingletonRegistrationException">Thrown when the child registers a service with <see cref="Lifetime.Singleton"/> other than through <see cref="IContainerBuilder.RegisterInstance{TService}"/>.</exception>
        /// <exception cref="Exceptions.CyclicDependencyException">Thrown when the child's registrations form a cycle, alone or through this scope's and its ancestors' registrations.</exception>
        IObjectResolver CreateChildScope(Action<IContainerBuilder> configure);

        /// <summary>
        /// Injects dependencies into the provided instance using the dependency injection container.
        /// </summary>
        /// <param name="instance">The object instance into which dependencies should be injected.</param>
        void Inject(object instance);
    }
}