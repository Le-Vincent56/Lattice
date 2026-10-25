using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// Scene host a coordinator drives explicitly: it never builds a resolver and does nothing on its own.
    /// <list type="number">
    ///     <item>
    ///         It implements no Unity message but <c>OnDestroy</c>. A host nobody attaches stays
    ///         <see cref="ScopeHostState.Detached"/>, and the objects in its scene stay unbound and uninitialized
    ///     </item>
    ///     <item>
    ///         <see cref="Attach"/> hands it a built-in resolver while it is detached, before or after <c>Awake</c>, on
    ///         an active or inactive GameObject, and says who owns it
    ///     </item>
    ///     <item>
    ///         An <see cref="ScopeOwnership.Owned"/> resolver is shut down when the host shuts down or is destroyed; a
    ///         <see cref="ScopeOwnership.Borrowed"/> one never is. A borrowed scope is prepared by one host at a time
    ///     </item>
    ///     <item>
    ///         <see cref="PrepareAsync(CancellationToken)"/> binds the batch (the scene's root GameObjects at the call,
    ///         inactive ones included, or the roots given), runs the scope's initialization pass, initializes the
    ///         batch's <see cref="IInitializable"/> components once, then runs and awaits the scope's startup. It
    ///         activates and deactivates nothing
    ///     </item>
    ///     <item>
    ///         The host becomes <see cref="ScopeHostState.Ready"/> only when every step succeeded while it was neither
    ///         cancelled nor shutting down. A failed step, or a cancellation once preparation began, makes it
    ///         <see cref="ScopeHostState.Faulted"/> until it is shut down
    ///     </item>
    ///     <item>
    ///         <see cref="WaitUntilReadyAsync"/> may be awaited by any number of callers
    ///     </item>
    ///     <item>
    ///         <see cref="ShutdownAsync"/> cancels a running prepare, waits for it to settle, shuts an owned resolver
    ///         down, clears <see cref="Resolver"/> and detaches. A new scope may then be attached, and preparing binds
    ///         and initializes the scene again, so a recycled scene's <c>Initialize</c> bodies must be repeat-safe
    ///     </item>
    ///     <item>
    ///         <c>OnDestroy</c> (a scene unload, <c>Destroy</c>) starts the same shutdown without waiting and logs each
    ///         failure as its own entry. Prefer awaiting <see cref="ShutdownAsync"/> before unloading. A host whose
    ///         GameObject was never active receives no <c>OnDestroy</c>; shut it down explicitly
    ///     </item>
    ///     <item>
    ///         Every member is called on Unity's main thread, and its awaits resume there; a public method called on
    ///         another thread is refused with <see cref="InvalidOperationException"/> before anything changes
    ///     </item>
    ///     <item>
    ///         Objects in the scene receive <c>Awake</c> and <c>OnEnable</c>, and may receive <c>Start</c>, before
    ///         preparation; their <c>[Inject]</c> fields are populated only once <c>PrepareAsync</c> bound them. Keep
    ///         dependency work in <c>Initialize</c>, or author the roots inactive and activate them after the host is
    ///         ready. Gameplay gating stays with the game
    ///     </item>
    /// </list>
    /// </summary>
    public sealed class ExplicitScopeHost : MonoBehaviour
    {
        // Attach refuses a second resolver.
        private const string AlreadyAttachedMessage = "This host already has a resolver; shut it down with ShutdownAsync before attaching another.";

        // Attach refuses a destroyed host.
        private const string DestroyedMessage = "This host was destroyed; attach the resolver to a live host.";

        // Attach refuses a disposed resolver.
        private const string ResolverDisposedMessage = "The resolver is disposed and cannot be attached.";

        // Attach refuses a resolver that is shutting down.
        private const string ResolverStoppingMessage = "The resolver is shutting down and cannot be attached.";

        // Attach refuses a resolver whose pass or startup another caller is running; {0} is its state.
        private const string ResolverBusyFormat = "The resolver is {0}: a pass or startup is running elsewhere; attach it before or after that call.";

        // PrepareAsync refuses a detached host.
        private const string PrepareDetachedMessage = "Attach a resolver before PrepareAsync.";

        // PrepareAsync refuses a host that is shutting down.
        private const string StoppingMessage = "This host is stopping.";

        // PrepareAsync refuses a faulted host; {0} is the fault's message.
        private const string FaultedFormat = "This host is faulted and must be shut down before it is attached to a new scope: {0}";

        // PrepareAsync refuses a host whose preparation was cancelled.
        private const string CanceledFaultMessage = "This host's preparation was cancelled; shut it down before attaching a new scope.";

        // PrepareAsync refuses a second call while one runs.
        private const string PreparingMessage = "PrepareAsync is already running on this host; await WaitUntilReadyAsync instead.";

        // WaitUntilReadyAsync refuses a detached host.
        private const string WaitDetachedMessage = "Attach a resolver before WaitUntilReadyAsync.";

        // WaitUntilReadyAsync refuses a caller inside the startup it would wait for.
        private const string WaitInsideStartupMessage = "WaitUntilReadyAsync cannot wait for a startup it is called from.";

        // ShutdownAsync refuses a caller inside the startup it would wait for.
        private const string ShutdownInsideStartupMessage = "ShutdownAsync cannot wait for a startup it is called from; shut the host down from its coordinator.";

        // The message of the AggregateException a shutdown throws when a cancellation callback or the disposal threw.
        private const string ShutdownFailureMessage = "One or more objects threw while the host shut its scope down; the scope was still shut down.";

        // The settle source of the prepare whose startup is awaiting on this logical call chain. Set immediately before
        // the startup await, so it flows into every participant and whatever a participant starts; the async method's
        // own execution context keeps it from reaching the caller. Compared with the current prepare's settle source,
        // so work left over from an earlier attach is never taken for the current startup.
        private static readonly AsyncLocal<TaskCompletionSource<bool>> _runningPrepare = new AsyncLocal<TaskCompletionSource<bool>>();

        // The attached resolver; null while detached.
        private IObjectResolver _resolver;

        // Whether a shutdown disposes _resolver.
        private ScopeOwnership _ownership;

        // Where this host is. Every field is read and written on the main thread only, so there is no lock.
        private ScopeHostState _state;

        // Incremented by every Attach. A prepare remembers the generation it started in and changes nothing once it
        // differs, so a stale startup can never touch a later attach.
        private int _generation;

        // Cancelled by a shutdown; every prepare of this attach links its token to it. Disposed by whichever of the
        // shutdown and the attach's last prepare finishes last, so a linked source never outlives it.
        private CancellationTokenSource _attachCancellation;

        // The readiness every WaitUntilReadyAsync of this attach awaits: completed only by a current, successful
        // prepare; faulted or cancelled by a failed or cancelled prepare; cancelled by a shutdown. One per attach,
        // with RunContinuationsAsynchronously, and its fault observed by the host.
        private TaskCompletionSource<bool> _readiness;

        // Completed, never faulted, when the current prepare has left its finally; what a shutdown waits for. Null
        // before the first prepare of an attach. RunContinuationsAsynchronously, so a waiting shutdown never resumes
        // inside the prepare's finally.
        private TaskCompletionSource<bool> _prepareSettled;

        // Completed when the running shutdown has detached the host; what a joining ShutdownAsync awaits.
        private TaskCompletionSource<bool> _shutdownSettled;

        // The exception that faulted this host; quoted by a later PrepareAsync. Null unless Faulted.
        private Exception _fault;

        // Set by OnDestroy, so Attach refuses a destroyed host.
        private bool _destroyed;

        /// <summary>
        /// Where this host is: detached, attached, preparing, ready, faulted or stopping.
        /// </summary>
        public ScopeHostState State => _state;

        /// <summary>
        /// The attached resolver; null while the host is detached, before an attach and after a shutdown.
        /// </summary>
        public IObjectResolver Resolver => _resolver;

        /// <summary>
        /// Starts this host's shutdown without waiting for it, when a resolver is attached and no shutdown runs: an
        /// owned resolver is shut down as soon as a running startup settles, at once when none runs, and each failure
        /// is logged as its own entry. Destroyed from inside its own unsettled startup, the host does not wait for
        /// that startup: it disposes an owned resolver at once.
        /// </summary>
        private void OnDestroy()
        {
            _destroyed = true;

            if (_state is ScopeHostState.Detached or ScopeHostState.Stopping) return;

            _ = ShutdownFromDestroyAsync(IsInsideOwnStartup());
        }

        /// <summary>
        /// Hands this host a built-in resolver while it is detached, before or after <c>Awake</c>, on an active or
        /// inactive GameObject, and says who disposes it. Reads no scene and prepares nothing. A resolver that is
        /// created, initialized, ready or faulted is accepted; a faulted one makes <c>PrepareAsync</c> report its
        /// fault. The checks run in the order of the exceptions below, and nothing changes when one throws.
        /// </summary>
        /// <param name="resolver">The resolver to host; must be the built-in scope.</param>
        /// <param name="ownership"><see cref="ScopeOwnership.Owned"/> when this host shuts the resolver down; <see cref="ScopeOwnership.Borrowed"/> when the attacher keeps it.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when called off Unity's main thread; when this host was destroyed; when a resolver is already
        /// attached; when <paramref name="resolver"/> is not the built-in scope; and when the resolver is running a
        /// pass or startup elsewhere or is shutting down.
        /// </exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="resolver"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="ownership"/> is neither <see cref="ScopeOwnership.Borrowed"/> nor <see cref="ScopeOwnership.Owned"/>.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when <paramref name="resolver"/> is disposed.</exception>
        public void Attach(IObjectResolver resolver, ScopeOwnership ownership)
        {
            ScopeHostEngine.ThrowIfNotMainThread(nameof(Attach));

            if (resolver == null) throw new ArgumentNullException(nameof(resolver));

            if (ownership != ScopeOwnership.Borrowed && ownership != ScopeOwnership.Owned)
                throw new ArgumentOutOfRangeException(nameof(ownership));

            // _destroyed covers a host whose OnDestroy ran; Unity's destroyed-object check also covers a host destroyed
            // while its GameObject was never active, which receives no OnDestroy.
            if (_destroyed || this == null) throw new InvalidOperationException(DestroyedMessage);

            if (_state != ScopeHostState.Detached) throw new InvalidOperationException(AlreadyAttachedMessage);

            // Throws for a resolver that is not the built-in scope.
            LifecycleState state = resolver.GetLifecycleState();
            if (state == LifecycleState.Disposed)
                throw new ObjectDisposedException(nameof(IObjectResolver), ResolverDisposedMessage);

            if (state == LifecycleState.Initializing || state == LifecycleState.Starting)
                throw new InvalidOperationException(string.Format(ResolverBusyFormat, state));

            if (state == LifecycleState.Stopping) throw new InvalidOperationException(ResolverStoppingMessage);

            _resolver = resolver;
            _ownership = ownership;
            _generation++;
            _attachCancellation = new CancellationTokenSource();
            _readiness = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ObserveReadiness();
            _prepareSettled = null;
            _fault = null;
            _state = ScopeHostState.Attached;
        }

        /// <summary>
        /// Prepares this host's scene: fixes the batch as the scene's root GameObjects at the call, inactive roots
        /// included, binds every MonoBehaviour beneath them, runs the scope's initialization pass, initializes the
        /// batch's <see cref="IInitializable"/> components once each, then runs the scope's startup and awaits it on
        /// the caller's context. Activates nothing. A call on a ready host prepares nothing; a call while one runs is
        /// refused. Every failure arrives through the returned task.
        /// </summary>
        /// <param name="cancellationToken">Already cancelled at the call, it changes nothing; cancelled once preparation began, it cancels the scope's startup and faults the host.</param>
        /// <returns>
        /// A task that completes when the host is ready. It is cancelled when the token was cancelled at the call (the
        /// host stays attached), or when a cancellation or a shutdown stopped the preparation; it is faulted with the
        /// failing step's exception, which faults the host, and with <see cref="InvalidOperationException"/> for a
        /// refusal, which changes nothing.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown, through the task, when called off Unity's main thread, or when the host is detached, stopping,
        /// faulted (the fault as inner exception) or already preparing.
        /// </exception>
        /// <exception cref="OperationCanceledException">Thrown, through the task, when a cancellation or a shutdown stopped the preparation.</exception>
        /// <exception cref="Exceptions.DependencyResolutionException">
        /// Thrown, through the task, when a step failed: a bind (for example a missing registration), or a service's,
        /// component's or participant's <c>Initialize</c> or <c>StartAsync</c> (<see cref="Exceptions.LifecycleException"/>);
        /// the host is then faulted.
        /// </exception>
        /// <exception cref="ArgumentException">Thrown, through the task and without any change, by Unity when the host's scene is valid but not loaded.</exception>
        public Task PrepareAsync(CancellationToken cancellationToken)
        {
            Task task = PrepareCoreAsync(null, true, cancellationToken);
            task.ContinueWith(ObserveFault, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }

        /// <summary>
        /// Prepares the given roots as the batch, as <see cref="PrepareAsync(CancellationToken)"/> prepares the scene's
        /// roots. The batch is copied from <paramref name="roots"/> at the call; null and destroyed entries are
        /// skipped. Every failure arrives through the returned task.
        /// </summary>
        /// <param name="roots">The root GameObjects to prepare.</param>
        /// <param name="cancellationToken">Already cancelled at the call, it changes nothing; cancelled once preparation began, it cancels the scope's startup and faults the host.</param>
        /// <returns>
        /// A task that completes when the host is ready, cancelled or faulted as for
        /// <see cref="PrepareAsync(CancellationToken)"/>, and faulted with <see cref="ArgumentNullException"/>, without
        /// any change, when <paramref name="roots"/> is null.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown, through the task and without any change, when <paramref name="roots"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown, through the task, when called off Unity's main thread, or when the host is detached, stopping,
        /// faulted (the fault as inner exception) or already preparing.
        /// </exception>
        /// <exception cref="OperationCanceledException">Thrown, through the task, when a cancellation or a shutdown stopped the preparation.</exception>
        /// <exception cref="Exceptions.DependencyResolutionException">
        /// Thrown, through the task, when a step failed: a bind (for example a missing registration), or a service's,
        /// component's or participant's <c>Initialize</c> or <c>StartAsync</c> (<see cref="Exceptions.LifecycleException"/>);
        /// the host is then faulted.
        /// </exception>
        public Task PrepareAsync(IReadOnlyList<GameObject> roots, CancellationToken cancellationToken)
        {
            Task task = PrepareCoreAsync(roots, false, cancellationToken);
            task.ContinueWith(ObserveFault, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }

        /// <summary>
        /// Waits until this host is ready. Any number of callers may wait: each wait completes when the host becomes
        /// ready, faults with the host's fault, and is cancelled when the host shuts down before it is ready or when
        /// the caller's own token is cancelled (that wait only). Every failure arrives through the returned task.
        /// </summary>
        /// <param name="cancellationToken">Cancels this wait only.</param>
        /// <returns>
        /// A task that completes when the host is ready; faulted with the host's fault, cancelled by a shutdown before
        /// ready or by <paramref name="cancellationToken"/>, and faulted with <see cref="InvalidOperationException"/>
        /// for a refusal.
        /// </returns>
        /// <exception cref="InvalidOperationException">Thrown, through the task, when called off Unity's main thread, when the host is detached, or when called from inside the host's own startup, which it would wait for.</exception>
        /// <exception cref="OperationCanceledException">Thrown, through the task, when the host shut down before it was ready, or when <paramref name="cancellationToken"/> was cancelled.</exception>
        public async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            ScopeHostEngine.ThrowIfNotMainThread(nameof(WaitUntilReadyAsync));

            if (_state == ScopeHostState.Detached) throw new InvalidOperationException(WaitDetachedMessage);

            if (IsInsideOwnStartup()) throw new InvalidOperationException(WaitInsideStartupMessage);

            Task readiness = _readiness.Task;
            if (!await WaitOrCancelAsync(readiness, cancellationToken)) throw new OperationCanceledException(cancellationToken);

            // Rethrows the readiness fault or cancellation, if any.
            await readiness;
        }

        /// <summary>
        /// Shuts this host down: cancels a running prepare, and with it the scope's startup, waits for it to settle,
        /// shuts an owned resolver down with <see cref="ResolverLifecycleExtensions.ShutdownAsync"/> (or disposes it
        /// with <c>Dispose</c>, which does not wait, when the core refuses to wait because the caller is inside a
        /// startup of that scope or a descendant), clears <see cref="Resolver"/> and leaves the host detached. A
        /// borrowed resolver is never disposed. A detached host returns at once; a call from inside the host's own
        /// startup is refused; a concurrent call joins the running shutdown and completes when it ends, without its
        /// failures, or stops waiting when its own token is cancelled. Afterwards <see cref="Attach"/> may hand the
        /// host a new scope. Every failure arrives through the returned task.
        /// </summary>
        /// <param name="cancellationToken">Bounds the waits, a joining call's included; an owned resolver is shut down anyway.</param>
        /// <returns>
        /// A task that completes once the host is detached. It is faulted with an <see cref="AggregateException"/>
        /// listing each cleanup failure (the scope was still shut down), cancelled when
        /// <paramref name="cancellationToken"/> stopped a wait, and faulted with <see cref="InvalidOperationException"/>,
        /// without any change, for a refusal.
        /// </returns>
        /// <exception cref="InvalidOperationException">Thrown, through the task and without any change, when called off Unity's main thread or from inside the host's own startup.</exception>
        /// <exception cref="AggregateException">Thrown, through the task, when a cancellation callback or the owned resolver's disposal threw; the host is detached and the scope shut down.</exception>
        /// <exception cref="OperationCanceledException">
        /// Thrown, through the task, when <paramref name="cancellationToken"/> stopped a wait: for the call that started
        /// the shutdown, the host is detached and an owned scope shut down; for a joining call, the running shutdown
        /// continues.
        /// </exception>
        public async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            ScopeHostEngine.ThrowIfNotMainThread(nameof(ShutdownAsync));

            if (_state == ScopeHostState.Detached) return;

            if (IsInsideOwnStartup()) throw new InvalidOperationException(ShutdownInsideStartupMessage);

            if (_state == ScopeHostState.Stopping)
            {
                // Joins the running shutdown; its failures go to the call that started it.
                Task running = _shutdownSettled.Task;
                if (!await WaitOrCancelAsync(running, cancellationToken)) throw new OperationCanceledException(cancellationToken);

                return;
            }

            await ShutdownCoreAsync(cancellationToken, false);
        }

        /// <summary>
        /// Runs one prepare: refuses in the order detached, stopping, faulted, preparing; returns when ready; returns
        /// cancelled for a token already cancelled; reads the batch; all of that without changing anything. Then turns
        /// the host <see cref="ScopeHostState.Preparing"/>, binds and initializes the batch through the shared engine,
        /// and awaits the scope's startup with a token linked to the caller's and the attach's, on the caller's
        /// context. Its outcome is decided only while it is current (same generation, still preparing): ready on
        /// success; faulted with readiness cancelled for a cancellation; faulted with readiness faulted for any other
        /// exception. A prepare that is no longer current changes nothing and ends with what it observed, so no
        /// failure is lost. Its finally completes the settle signal, disposes the linked source, then disposes the
        /// attach's source when the host has moved on from that attach.
        /// </summary>
        /// <param name="roots">The given batch; ignored when <paramref name="useSceneRoots"/> is true.</param>
        /// <param name="useSceneRoots">True to use the host scene's root GameObjects at the call.</param>
        /// <param name="cancellationToken">The caller's token.</param>
        /// <returns>A task that completes when the host is ready.</returns>
        /// <exception cref="InvalidOperationException">Thrown, through the task, for the main-thread refusal and the state refusals.</exception>
        /// <exception cref="OperationCanceledException">Thrown, through the task, when the token was cancelled at the call, or when a cancellation or a shutdown stopped the preparation.</exception>
        /// <exception cref="ArgumentNullException">Thrown, through the task, when <paramref name="roots"/> is null and <paramref name="useSceneRoots"/> is false.</exception>
        /// <exception cref="Exceptions.DependencyResolutionException">Thrown, through the task, when a bind, the pass, a component's initialization or the startup failed.</exception>
        private async Task PrepareCoreAsync(IReadOnlyList<GameObject> roots, bool useSceneRoots, CancellationToken cancellationToken)
        {
            ScopeHostEngine.ThrowIfNotMainThread(nameof(PrepareAsync));

            if (_state == ScopeHostState.Detached) throw new InvalidOperationException(PrepareDetachedMessage);

            if (_state == ScopeHostState.Stopping) throw new InvalidOperationException(StoppingMessage);

            if (_state == ScopeHostState.Faulted)
            {
                if (_fault is OperationCanceledException) throw new InvalidOperationException(CanceledFaultMessage, _fault);

                throw new InvalidOperationException(string.Format(FaultedFormat, _fault.Message), _fault);
            }

            if (_state == ScopeHostState.Preparing) throw new InvalidOperationException(PreparingMessage);

            if (_state == ScopeHostState.Ready) return;

            cancellationToken.ThrowIfCancellationRequested();

            GameObject[] batch = useSceneRoots ? ScopeHostEngine.GetSceneRoots(gameObject) : CopyRoots(roots);

            // Build in locals and change state only once every call that can throw has returned, so a refusal from
            // here on still changes nothing.
            int generation = _generation;
            IObjectResolver resolver = _resolver;
            CancellationTokenSource attach = _attachCancellation;
            TaskCompletionSource<bool> settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attach.Token);
            _prepareSettled = settled;
            _state = ScopeHostState.Preparing;

            try
            {
                ScopeHostEngine.Prepare(resolver, batch);
                ThrowIfPrepareStopped(generation, linked.Token);

                // A WaitUntilReadyAsync or ShutdownAsync a participant calls on this host would wait for this very
                // startup; the marker lets them refuse instead.
                _runningPrepare.Value = settled;
                await resolver.RunAsyncStartablesAsync(linked.Token);

                ThrowIfPrepareStopped(generation, linked.Token);
                _state = ScopeHostState.Ready;
                _readiness.TrySetResult(true);
            }
            catch (OperationCanceledException exception)
            {
                if (IsCurrent(generation)) FaultPrepare(exception, true);
                throw;
            }
            catch (Exception exception)
            {
                if (IsCurrent(generation)) FaultPrepare(exception, false);
                throw;
            }
            finally
            {
                settled.TrySetResult(true);
                linked.Dispose();
                if (!IsAttachedTo(generation)) attach.Dispose();
            }
        }

        /// <summary>
        /// Runs the shared shutdown: turns the host <see cref="ScopeHostState.Stopping"/> and creates the signal joiners
        /// await, both before any await; cancels the attach's source (callback failures collected) and readiness;
        /// unless <paramref name="immediate"/>, waits for the running prepare to settle or the token, whichever is
        /// first; then, for an owned resolver, awaits the core's shutdown, falling back to <c>Dispose</c> when the core
        /// refuses to wait or when <paramref name="immediate"/> is set. Its finally clears <see cref="Resolver"/>,
        /// detaches the host, disposes the attach's source once the prepare has settled, and releases the joiners.
        /// </summary>
        /// <param name="cancellationToken">Bounds the waits; never stops the disposal.</param>
        /// <param name="immediate">True when called from inside this host's own unsettled startup: waits for nothing and disposes an owned resolver at once.</param>
        /// <returns>A task that completes once the host is detached.</returns>
        /// <exception cref="AggregateException">Thrown, through the task, when a cancellation callback or the disposal threw; the host is detached.</exception>
        /// <exception cref="OperationCanceledException">Thrown, through the task, when <paramref name="cancellationToken"/> stopped a wait; the host is detached.</exception>
        private async Task ShutdownCoreAsync(CancellationToken cancellationToken, bool immediate)
        {
            IObjectResolver resolver = _resolver;
            ScopeOwnership ownership = _ownership;
            CancellationTokenSource attach = _attachCancellation;
            TaskCompletionSource<bool> settled = _prepareSettled;
            _state = ScopeHostState.Stopping;
            _shutdownSettled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            List<Exception> failures = new List<Exception>();
            bool stoppedByToken = false;

            try
            {
                try
                {
                    attach.Cancel();
                }
                catch (AggregateException exception)
                {
                    failures.AddRange(exception.Flatten().InnerExceptions);
                }

                _readiness.TrySetCanceled();

                if (!immediate && settled != null && !await WaitOrCancelAsync(settled.Task, cancellationToken))
                {
                    stoppedByToken = true;
                }

                if (ownership == ScopeOwnership.Owned)
                {
                    bool disposeNow = immediate;
                    if (!disposeNow)
                    {
                        try
                        {
                            await resolver.ShutdownAsync(cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            stoppedByToken = true;
                        }
                        catch (AggregateException exception)
                        {
                            failures.AddRange(exception.Flatten().InnerExceptions);
                        }
                        catch (InvalidOperationException)
                        {
                            // The core refuses to wait when the caller is inside a startup of this scope or a
                            // descendant; dispose without waiting, so an owned scope is never left undisposed.
                            disposeNow = true;
                        }
                    }

                    if (disposeNow)
                    {
                        try
                        {
                            resolver.Dispose();
                        }
                        catch (AggregateException exception)
                        {
                            failures.AddRange(exception.InnerExceptions);
                        }
                    }
                }
            }
            finally
            {
                _resolver = null;
                _state = ScopeHostState.Detached;
                if (settled == null || settled.Task.IsCompleted) attach.Dispose();
                _shutdownSettled.TrySetResult(true);
            }

            if (failures.Count > 0) throw new AggregateException(ShutdownFailureMessage, failures);

            if (stoppedByToken) throw new OperationCanceledException(cancellationToken);
        }

        /// <summary>
        /// Runs the shutdown <c>OnDestroy</c> starts and observes it fully, so its task never faults: logs each failure
        /// of the aggregate as its own entry, and any other exception as one entry. The host may be gone by the time
        /// it fails, so no context object is passed.
        /// </summary>
        /// <param name="immediate">True when the host was destroyed from inside its own unsettled startup.</param>
        /// <returns>A task that completes once the host is detached; it never faults.</returns>
        private async Task ShutdownFromDestroyAsync(bool immediate)
        {
            try
            {
                await ShutdownCoreAsync(CancellationToken.None, immediate);
            }
            catch (AggregateException exception)
            {
                ScopeHostEngine.LogFailures(exception, null);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        /// <summary>
        /// Returns whether the prepare that started in <paramref name="generation"/> may still decide the host's
        /// outcome: the attach is unchanged and the host is still preparing.
        /// </summary>
        /// <param name="generation">The generation the prepare started in.</param>
        /// <returns>True when the prepare is current.</returns>
        private bool IsCurrent(int generation) => generation == _generation && _state == ScopeHostState.Preparing;

        /// <summary>
        /// Returns whether the host is still attached in <paramref name="generation"/>, that is, neither re-attached nor
        /// detached since; while it is, the attach's cancellation source is the shutdown's to dispose.
        /// </summary>
        /// <param name="generation">The generation the prepare started in.</param>
        /// <returns>True when the host is still on that attach.</returns>
        private bool IsAttachedTo(int generation) => generation == _generation && _state != ScopeHostState.Detached;

        /// <summary>
        /// Stops a prepare before and after its startup await when it is no longer current (a shutdown began or the
        /// host was re-attached) or its token is cancelled.
        /// </summary>
        /// <param name="generation">The generation the prepare started in.</param>
        /// <param name="cancellationToken">The prepare's linked token.</param>
        /// <exception cref="OperationCanceledException">Thrown when the prepare is no longer current or <paramref name="cancellationToken"/> is cancelled.</exception>
        private void ThrowIfPrepareStopped(int generation, CancellationToken cancellationToken)
        {
            if (!IsCurrent(generation)) throw new OperationCanceledException();

            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Faults the host with <paramref name="exception"/> and ends readiness: cancelled for a cancellation, faulted
        /// with the exception otherwise. Called only by a current prepare.
        /// </summary>
        /// <param name="exception">The exception that ended the prepare.</param>
        /// <param name="canceled">True when <paramref name="exception"/> is a cancellation.</param>
        private void FaultPrepare(Exception exception, bool canceled)
        {
            _state = ScopeHostState.Faulted;
            _fault = exception;
            if (canceled)
            {
                _readiness.TrySetCanceled();
            }
            else
            {
                _readiness.TrySetException(exception);
            }
        }

        /// <summary>
        /// Returns whether the calling code runs inside this host's current, unsettled startup: the running-prepare
        /// marker holds the current prepare's settle source and that source has not completed. The marker also flows
        /// into work a participant started without awaiting it, which is treated the same way until the startup
        /// settles.
        /// </summary>
        /// <returns>True when a wait from here would wait for the caller's own startup.</returns>
        private bool IsInsideOwnStartup()
        {
            return _prepareSettled != null && ReferenceEquals(_runningPrepare.Value, _prepareSettled) && !_prepareSettled.Task.IsCompleted;
        }

        /// <summary>
        /// Observes the current readiness task's fault, so a failure nobody awaited is not reported as an unobserved
        /// task exception.
        /// </summary>
        private void ObserveReadiness()
        {
            _readiness.Task.ContinueWith(ObserveFault, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// <summary>
        /// Reads a faulted task's exception, which marks it observed.
        /// </summary>
        /// <param name="task">The faulted task.</param>
        private static void ObserveFault(Task task) => _ = task.Exception;

        /// <summary>
        /// Copies a given batch at the call, so later changes to the caller's list do not reach the prepare. Null and
        /// destroyed entries are kept; the bind and initialize walks skip them.
        /// </summary>
        /// <param name="roots">The given roots.</param>
        /// <returns>A new array with the same entries.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="roots"/> is null.</exception>
        private static GameObject[] CopyRoots(IReadOnlyList<GameObject> roots)
        {
            if (roots == null) throw new ArgumentNullException(nameof(roots));

            GameObject[] copy = new GameObject[roots.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = roots[i];
            }

            return copy;
        }

        /// <summary>
        /// Waits until <paramref name="task"/> completes or <paramref name="cancellationToken"/> is cancelled, whichever
        /// comes first, continuing on the caller's context, without observing the task's outcome. Returns at once
        /// when the task has already completed. A token whose source the caller disposed without cancelling it can
        /// never end the wait: when registering on it throws, the wait covers the task alone.
        /// </summary>
        /// <param name="task">The task to wait for.</param>
        /// <param name="cancellationToken">Ends the wait early.</param>
        /// <returns>True when the task completed first; false when the token ended the wait.</returns>
        private static async Task<bool> WaitOrCancelAsync(Task task, CancellationToken cancellationToken)
        {
            if (task.IsCompleted) return true;

            TaskCompletionSource<bool> canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration;
            try
            {
                registration = cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state).TrySetResult(true), canceled);
            }
            catch (ObjectDisposedException)
            {
                // Some runtimes refuse to register on a token whose source the caller disposed; that token can never be
                // cancelled, so only the task ends the wait.
                await Task.WhenAny(task);
                return true;
            }

            using (registration)
            {
                Task first = await Task.WhenAny(task, canceled.Task);
                return ReferenceEquals(first, task);
            }
        }
    }
}