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
        /// Creates a new child scope within the dependency injection container, allowing
        /// additional configurations to be applied specifically to the child scope.
        /// </summary>
        /// <param name="configure">An action to configure the child scope by adding registrations
        /// or services through the provided container builder.</param>
        /// <returns>An instance of <see cref="IObjectResolver"/> representing the created child scope.</returns>
        IObjectResolver CreateChildScope(Action<IContainerBuilder> configure);

        /// <summary>
        /// Injects dependencies into the provided instance using the dependency injection container.
        /// </summary>
        /// <param name="instance">The object instance into which dependencies should be injected.</param>
        void Inject(object instance);
    }
}