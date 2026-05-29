using System;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime.Internal;

namespace Didionysymus.Lattice.Runtime
{
    /// <summary>
    /// Extension methods on <see cref="IObjectResolver"/> for lifecycle hooks.
    /// These route to internal methods on the concrete <see cref="Scope"/> resolver.
    /// </summary>
    public static class ResolverLifecycleExtensions
    {
        /// <summary>
        /// Runs <see cref="IInitializable.Initialize"/> on every materialized
        /// <see cref="IInitializable"/> instance in this scope and the root singleton cache.
        /// Idempotent: repeated calls do not re-initialize.
        /// </summary>
        /// <param name="resolver">The resolver. Must be the built-in <see cref="Scope"/> implementation.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="resolver"/> is not the built-in <see cref="Scope"/>
        /// (e.g., a user-supplied test double). The lifecycle hooks rely on internal cache
        /// access and cannot be polyfilled through the public <see cref="IObjectResolver"/> surface.
        /// </exception>
        public static void RunInitializables(this IObjectResolver resolver)
        {
            if (resolver is not Scope scope)
                throw new InvalidOperationException(
                    "RunInitializables is only supported on the built-in Scope resolver.");

            scope.RunInitializables();
        }

        /// <summary>
        /// Walks the resolver's local registry in registration order and awaits
        /// <see cref="IAsyncStartable.StartAsync"/> on every <see cref="IAsyncStartable"/>
        /// registration sequentially.
        /// </summary>
        /// <param name="resolver">The resolver. Must be the built-in <see cref="Scope"/> implementation.</param>
        /// <param name="cancellationToken">A task that completes when every IAsyncStartable's StartAsync has completed.</param>
        /// <exception cref="InvalidOperationException">Thrown when <paramref name="resolver"/> is not the built-in <see cref="Scope"/>.</exception>
        /// <returns></returns>
        public static Task RunAsyncStartablesAsync(this IObjectResolver resolver, CancellationToken cancellationToken)
        {
            if (resolver is not Scope scope)
                throw new InvalidOperationException(
                    "RunAsyncStartablesAsync is only supported on the built-in Scope resolver.");

            return scope.RunAsyncStartablesAsync(cancellationToken);
        }
    }
}