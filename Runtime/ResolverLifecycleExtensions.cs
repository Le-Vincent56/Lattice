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
        /// implementation or decorator type implements <see cref="IInitializable"/> or <see cref="IAsyncStartable"/>,
        /// then calls <see cref="IInitializable.Initialize"/> once per instance, by reference, in creation order. Each
        /// scope runs its own pass, after its parent's. A pass after a successful pass initializes only instances
        /// created since. A failed pass faults the scope; dispose it and build a new one.
        /// </summary>
        /// <param name="resolver">The resolver. Must be the built-in <see cref="Scope"/> implementation.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="resolver"/> is not the built-in <see cref="Scope"/> (e.g., a user-supplied
        /// test double), when the scope is shutting down, when a pass is already running, when the scope is faulted,
        /// when the parent scope has not completed its pass, or, once startup has begun, when an initializable created
        /// since is not initialized (initialize it with <see cref="InitializeInstance"/>; with nothing pending the call
        /// does nothing).
        /// </exception>
        /// <exception cref="ObjectDisposedException">Thrown when the scope has been disposed, before or during the pass; a pass that observes the disposal stops before the next participant.</exception>
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
        /// <exception cref="InvalidOperationException">Thrown when <paramref name="resolver"/> is not the built-in <see cref="Scope"/>, when the scope is shutting down, has not completed its pass, is running one, or is faulted.</exception>
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
        /// Runs the scope's startup once: starts every <see cref="IAsyncStartable"/> the scope has cached, which
        /// includes every Singleton or Scoped startable the initialization pass built, once each by reference, in
        /// creation order, awaiting each <c>StartAsync</c> before the next. Requires <see cref="RunInitializables"/> on
        /// this scope and, for a child, a completed startup on the parent. A call after startup succeeded starts
        /// nothing; a call while it runs is refused. The token is checked before and between participants; a cancelled
        /// or failed startup faults the scope. Participants receive a token the scope owns, which lives until the scope
        /// is disposed: the caller's token cancels it only while startup runs, and <see cref="ShutdownAsync"/> and
        /// disposing the scope cancel it at any time until then, so a participant may keep it for background work that
        /// should end with the scope. Continues on the caller's synchronization context between participants: call it
        /// from the main thread and every <c>StartAsync</c> runs there. Await it; never block on the task.
        /// </summary>
        /// <param name="resolver">The resolver. Must be the built-in <see cref="Scope"/> implementation.</param>
        /// <param name="cancellationToken">Cancels startup before or between participants; while startup runs it also cancels the token participants receive, and cancelling it after startup ended does not reach that token.</param>
        /// <returns>
        /// A task that completes when every participant has started. It is cancelled when the token, a shutdown or
        /// disposal stopped startup, and faulted with <see cref="Exceptions.LifecycleException"/> when a participant
        /// failed, with <see cref="InvalidOperationException"/> when startup is running, the scope is shutting down or
        /// faulted, its pass or its parent's startup has not completed, or a cached participant is not initialized, and
        /// with <see cref="ObjectDisposedException"/> when the scope is disposed.
        /// </returns>
        /// <exception cref="InvalidOperationException">Thrown when <paramref name="resolver"/> is not the built-in <see cref="Scope"/>.</exception>
        public static Task RunAsyncStartablesAsync(this IObjectResolver resolver, CancellationToken cancellationToken)
        {
            if (resolver is not Scope scope)
                throw new InvalidOperationException("RunAsyncStartablesAsync is only supported on the built-in Scope resolver.");

            return scope.RunAsyncStartablesAsync(cancellationToken);
        }
        
        /// <summary>
        /// Shuts the scope down in order: stops lifecycle work on the scope and its descendants, cancels their
        /// participant tokens, waits for any running startup to settle (the running participant returns), then disposes
        /// the scope on the caller's synchronization context. Resolves keep working while it waits; lifecycle calls are
        /// refused. The token bounds the wait: when it is cancelled the scope is disposed at once and the task completes
        /// as cancelled. On a scope already disposed, the task completes once that disposal has finished. Never await it
        /// from inside a <c>StartAsync</c> of the scope or a descendant while that startup runs; <c>Dispose</c> is the
        /// immediate path, which cancels and disposes without waiting. Await it; never block on the task.
        /// </summary>
        /// <param name="resolver">The resolver. Must be the built-in <see cref="Scope"/> implementation.</param>
        /// <param name="cancellationToken">Bounds the wait for running startup work; it never stops the disposal.</param>
        /// <returns>
        /// A task that completes once the scope is disposed. It is cancelled when <paramref name="cancellationToken"/>
        /// stopped the wait, faulted with an <see cref="AggregateException"/> listing each failure when a child scope's
        /// disposal, a tracked object's <c>Dispose</c> or a cancellation callback threw, and faulted with
        /// <see cref="InvalidOperationException"/>, without changing the scope, when called from inside a running
        /// startup of the scope or a descendant.
        /// </returns>
        /// <exception cref="InvalidOperationException">Thrown when <paramref name="resolver"/> is not the built-in <see cref="Scope"/>.</exception>
        public static Task ShutdownAsync(this IObjectResolver resolver, CancellationToken cancellationToken)
        {
            if (resolver is not Scope scope)
                throw new InvalidOperationException("ShutdownAsync is only supported on the built-in Scope resolver.");

            return scope.ShutdownAsync(cancellationToken);
        }
    }
}