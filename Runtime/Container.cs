using System;
using Didionysymus.Lattice.Runtime.Internal;

namespace Didionysymus.Lattice.Runtime
{
    /// <summary>
    /// Public entry point for constructing a container. Builds an <see cref="IObjectResolver"/>
    /// from a caller-supplied configuration delegate. The returned resolver is the root scope;
    /// dispose it to dispose all tracked IDisposable instances and child scopes.
    /// </summary>
    public static class Container
    {
        /// <summary>
        /// Builds a new container with the registrations described by <paramref name="configure"/>.
        /// </summary>
        /// <param name="configure">User-supplied delegate that registers services on a fresh <see cref="IContainerBuilder"/>.</param>
        /// <returns>A root <see cref="IObjectResolver"/> ready for resolution.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is null.</exception>
        public static IObjectResolver Build(Action<IContainerBuilder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            ContainerBuilder builder = new ContainerBuilder();
            configure.Invoke(builder);

            // Promote PreserveClosedGenerics declarations into the closed registry before validation
            // so the validator's closed-graph walk covers them too. Root scope has no parent chain.
            builder.Registry.PrebuildPreservedClosedGenerics(parentChain: null);

            // Root scope has no parents; validate against an empty parent chain.
            // Array.Empty<T>() avoids allocation and avoids pulling in Linq
            DependencyGraphValidator.Validate(builder.Registry, parentChain: Array.Empty<Registry>());

            return new Scope(builder.Registry, parent: null);
        }
    }
}