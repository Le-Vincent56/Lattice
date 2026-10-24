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
        /// Runs the scope's initialization pass: builds every Singleton or Scoped registration of this scope whose
        /// implementation or decorator type implements <see cref="IInitializable"/>, then calls
        /// <see cref="IInitializable.Initialize"/> once per instance, by reference, in creation order. Each scope runs
        /// its own pass, after its parent's. A pass after a successful pass initializes only instances created since.
        /// A failed pass faults the scope; dispose it and build a new one.
        /// </summary>
        /// <param name="resolver">The resolver. Must be the built-in <see cref="Scope"/> implementation.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="resolver"/> is not the built-in <see cref="Scope"/> (e.g., a user-supplied
        /// test double), when a pass is already running, when the scope is faulted, or when the parent scope has
        /// not completed its pass.
        /// </exception>
        /// <exception cref="ObjectDisposedException">Thrown when the scope has been disposed.</exception>
        /// <exception cref="Exceptions.LifecycleException">Thrown when a participant's <c>Initialize</c> throws; the scope is then faulted.</exception>
        public static void RunInitializables(this IObjectResolver resolver)
        {
            if (resolver is not Scope scope)
                throw new InvalidOperationException("RunInitializables is only supported on the built-in Scope resolver.");

            scope.RunInitializables();
        }
        
        /// <summary>
        /// Initializes an externally supplied object once in this scope, by reference, through the same tracking the
        /// initialization pass uses: a scene component, a hand-built object, or a <c>RegisterInstance</c> value. The
        /// scope must have completed <see cref="RunInitializables"/>, so the object is never initialized before the
        /// services it was bound to.
        /// </summary>
        /// <param name="resolver">The resolver. Must be the built-in <see cref="Scope"/> implementation.</param>
        /// <param name="instance">The object to initialize. Objects that are not <see cref="IInitializable"/> are ignored.</param>
        /// <returns>True when <see cref="IInitializable.Initialize"/> ran; false when the object is not <see cref="IInitializable"/> or this scope already initialized it.</returns>
        /// <exception cref="InvalidOperationException">Thrown when <paramref name="resolver"/> is not the built-in <see cref="Scope"/>, when the scope has not completed its pass, is running one, or is faulted.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="instance"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the scope has been disposed.</exception>
        /// <exception cref="Exceptions.LifecycleException">Thrown when the object's <c>Initialize</c> throws; the scope is then faulted.</exception>
        public static bool InitializeInstance(this IObjectResolver resolver, object instance)
        {
            if (resolver is not Scope scope)
                throw new InvalidOperationException("InitializeInstance is only supported on the built-in Scope resolver.");

            return scope.InitializeInstance(instance);
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