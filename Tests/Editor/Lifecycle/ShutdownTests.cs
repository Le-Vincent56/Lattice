using System;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Internal;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Verifies awaitable shutdown: <c>ShutdownAsync</c> stops lifecycle work on the scope and its descendants,
    /// cancels their participant tokens, waits for running startup to return, then disposes the scope on the
    /// caller's context. Resolves keep working while it waits; its token bounds only the wait; disposal failures
    /// fault its task; a call from the scope's own running startup is refused; concurrent calls share one disposal;
    /// a child shut down alone leaves its parent ready; and a child created while its parent waits is disposed with
    /// it.
    ///
    /// Exercises <c>ResolverLifecycleExtensions.ShutdownAsync</c>, <c>Scope.ShutdownAsync</c>,
    /// <c>Scope.BeginStopping</c>, <c>Scope.IsInsideOwnStartup</c> and <c>Scope.WaitForSettleAsync</c>.
    /// </summary>
    [TestFixture]
    public sealed class ShutdownTests
    {
        /// <summary>
        /// Resets the static fixture state the tests read.
        /// </summary>
        [SetUp]
        public void Reset()
        {
            DisposalLog.Events.Clear();
            ShutdownCallingStartable.Target = null;
            DisposingCallbackStartable.Target = null;
        }

        /// <summary>
        /// With no startup running, shutdown disposes the scope and completes.
        /// </summary>
        [Test]
        public async Task ShutdownAsync_WhenNoStartupRan_DisposesScope()
        {
            IObjectResolver resolver = Container.Build(b => b.Register<InitTracking>(Lifetime.Singleton));
            resolver.RunInitializables();

            Exception caught = await TaskCapture.CatchAsync(resolver.ShutdownAsync(CancellationToken.None));

            Assert.IsNull(caught, "Shutdown completes");
            Assert.Throws<ObjectDisposedException>(() => resolver.Resolve<InitTracking>(), "Shutdown disposes the scope");
        }

        /// <summary>
        /// With a startup running, shutdown cancels the participant token at once but disposes nothing until the
        /// running participant returns.
        /// </summary>
        [Test]
        public async Task ShutdownAsync_WhenStartupRunning_CancelsTokenAndDisposesOnlyAfterParticipantReturns()
        {
            GatedStartable gated = new GatedStartable();
            LoggedDisposable service = new LoggedDisposable("svc");
            IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton);
                b.RegisterFactory<LoggedDisposable>(_ => service, Lifetime.Singleton);
            });
            resolver.Resolve<LoggedDisposable>();
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Task shutdown = resolver.ShutdownAsync(CancellationToken.None);
            bool tokenCanceled = gated.ReceivedToken.IsCancellationRequested;
            int disposedBeforeReturn = service.DisposeCount;
            bool completedBeforeReturn = shutdown.IsCompleted;
            gated.Gate.SetResult(true);
            Exception caught = await TaskCapture.CatchAsync(shutdown);
            _ = await TaskCapture.CatchAsync(startup);

            Assert.IsTrue(tokenCanceled, "Shutdown cancels the participant token at once");
            Assert.AreEqual(0, disposedBeforeReturn, "Nothing is disposed while the participant runs");
            Assert.IsFalse(completedBeforeReturn, "Shutdown waits for the running participant");
            Assert.AreEqual(1, service.DisposeCount, "The scope is disposed once the participant returned");
            Assert.IsNull(caught, "Shutdown completes");
        }

        /// <summary>
        /// Shutdown on the root waits for a startup running in a child scope before disposing it.
        /// </summary>
        [Test]
        public async Task ShutdownAsync_WhenChildStartupRunning_WaitsForChildParticipantBeforeDisposing()
        {
            GatedStartable gated = new GatedStartable();
            LoggedDisposable childService = new LoggedDisposable("child");
            IObjectResolver root = Container.Build(_ => { });
            root.RunInitializables();
            await root.RunAsyncStartablesAsync(CancellationToken.None);
            IObjectResolver child = root.CreateChildScope(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Scoped);
                b.RegisterFactory<LoggedDisposable>(_ => childService, Lifetime.Scoped);
            });
            child.Resolve<LoggedDisposable>();
            child.RunInitializables();
            Task startup = child.RunAsyncStartablesAsync(CancellationToken.None);

            Task shutdown = root.ShutdownAsync(CancellationToken.None);
            bool tokenCanceled = gated.ReceivedToken.IsCancellationRequested;
            int disposedBeforeReturn = childService.DisposeCount;
            gated.Gate.SetResult(true);
            Exception caught = await TaskCapture.CatchAsync(shutdown);
            _ = await TaskCapture.CatchAsync(startup);

            Assert.IsTrue(tokenCanceled, "Shutdown cancels the child's participant token");
            Assert.AreEqual(0, disposedBeforeReturn, "Nothing in the child is disposed while its participant runs");
            Assert.AreEqual(1, childService.DisposeCount, "The child is disposed once its participant returned");
            Assert.IsNull(caught, "Shutdown completes");
        }

        /// <summary>
        /// A token cancelled while shutdown waits ends the wait: the scope is disposed at once and the task completes
        /// as cancelled.
        /// </summary>
        [Test]
        public async Task ShutdownAsync_WhenTokenCanceledDuringWait_DisposesScopeAndCompletesAsCanceled()
        {
            GatedStartable gated = new GatedStartable();
            LoggedDisposable service = new LoggedDisposable("svc");
            using CancellationTokenSource source = new CancellationTokenSource();
            IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton);
                b.RegisterFactory<LoggedDisposable>(_ => service, Lifetime.Singleton);
            });
            resolver.Resolve<LoggedDisposable>();
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);
            Task shutdown = resolver.ShutdownAsync(source.Token);

            source.Cancel();
            Exception caught = await TaskCapture.CatchAsync(shutdown);
            int disposedAfterCancel = service.DisposeCount;
            Exception resolveFailure = Record(() => resolver.Resolve<LoggedDisposable>());
            gated.Gate.SetResult(true);
            _ = await TaskCapture.CatchAsync(startup);

            Assert.IsInstanceOf<OperationCanceledException>(caught, "Shutdown completes as cancelled");
            Assert.AreEqual(1, disposedAfterCancel, "The scope is disposed without waiting further");
            Assert.IsInstanceOf<ObjectDisposedException>(resolveFailure, "The scope is disposed");
        }

        /// <summary>
        /// A service whose <c>Dispose</c> throws faults the shutdown task with the disposal's
        /// <see cref="AggregateException"/>.
        /// </summary>
        [Test]
        public async Task ShutdownAsync_WhenServiceDisposeThrows_FaultsWithAggregateException()
        {
            IObjectResolver resolver = Container.Build(b => b.RegisterFactory<ThrowingDisposable>(_ => new ThrowingDisposable("svc"), Lifetime.Singleton));
            resolver.Resolve<ThrowingDisposable>();

            Exception caught = await TaskCapture.CatchAsync(resolver.ShutdownAsync(CancellationToken.None));

            Assert.IsInstanceOf<AggregateException>(caught, "Shutdown reports the disposal's failures");
            AggregateException aggregate = (AggregateException)caught;
            Assert.AreEqual(1, aggregate.InnerExceptions.Count, "One failure is listed");
            Assert.AreEqual("svc", aggregate.InnerExceptions[0].Message, "The failure is the throwing service's");
        }

        /// <summary>
        /// A participant that calls shutdown on its own scope while its startup runs is refused, and the scope is
        /// left as it was.
        /// </summary>
        [Test]
        public async Task ShutdownAsync_WhenCalledFromInsideStartAsync_ThrowsInvalidOperationException()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<ShutdownCallingStartable>(Lifetime.Singleton));
            ShutdownCallingStartable.Target = resolver;
            ShutdownCallingStartable startable = resolver.Resolve<ShutdownCallingStartable>();
            resolver.RunInitializables();
            _ = await TaskCapture.CatchAsync(resolver.RunAsyncStartablesAsync(CancellationToken.None));

            Exception refused = await TaskCapture.CatchAsync(startable.ReturnedTask);

            Assert.IsTrue(startable.ReturnedTask.IsFaulted, "A shutdown that would wait on itself is refused");
            Assert.IsInstanceOf<InvalidOperationException>(refused, "The refusal is an InvalidOperationException");
            Assert.AreEqual(LifecycleState.Ready, ((Scope)resolver).State, "The refusal changes nothing; startup completes");
        }

        /// <summary>
        /// While shutdown waits the scope is stopping: resolves still work, lifecycle calls are refused.
        /// </summary>
        [Test]
        public async Task ShutdownAsync_WhileWaiting_ScopeResolvesAndRefusesInitializeInstance()
        {
            GatedStartable gated = new GatedStartable();
            IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton);
                b.Register<InitTracking>(Lifetime.Singleton);
            });
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Task shutdown = resolver.ShutdownAsync(CancellationToken.None);
            LifecycleState state = ((Scope)resolver).State;
            Exception resolveFailure = Record(() => resolver.Resolve<InitTracking>());
            Exception initializeFailure = Record(() => resolver.InitializeInstance(new InitTracking()));
            gated.Gate.SetResult(true);
            _ = await TaskCapture.CatchAsync(startup);
            _ = await TaskCapture.CatchAsync(shutdown);

            Assert.AreEqual(LifecycleState.Stopping, state, "A waiting shutdown leaves the scope stopping");
            Assert.IsNull(resolveFailure, "Resolves keep working while shutdown waits");
            Assert.IsInstanceOf<InvalidOperationException>(initializeFailure, "Lifecycle calls are refused while shutdown waits");
        }

        /// <summary>
        /// Two shutdown calls share one shutdown: both wait for the running participant, both complete once the
        /// scope is disposed, and it is disposed once.
        /// </summary>
        [Test]
        public async Task ShutdownAsync_WhenCalledTwice_BothCompleteAfterDisposal()
        {
            GatedStartable gated = new GatedStartable();
            LoggedDisposable service = new LoggedDisposable("svc");
            IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton);
                b.RegisterFactory<LoggedDisposable>(_ => service, Lifetime.Singleton);
            });
            resolver.Resolve<LoggedDisposable>();
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Task first = resolver.ShutdownAsync(CancellationToken.None);
            Task second = resolver.ShutdownAsync(CancellationToken.None);
            bool eitherCompletedBeforeReturn = first.IsCompleted || second.IsCompleted;
            gated.Gate.SetResult(true);
            Exception firstFailure = await TaskCapture.CatchAsync(first);
            Exception secondFailure = await TaskCapture.CatchAsync(second);
            _ = await TaskCapture.CatchAsync(startup);

            Assert.IsFalse(eitherCompletedBeforeReturn, "Both calls wait for the running participant");
            Assert.IsNull(firstFailure, "The first call completes");
            Assert.IsNull(secondFailure, "The second call completes");
            Assert.AreEqual(1, service.DisposeCount, "The scope is disposed once");
        }

        /// <summary>
        /// A token a participant kept after startup is cancelled by shutdown before the scope's services are
        /// disposed.
        /// </summary>
        [Test]
        public async Task ShutdownAsync_AfterStartup_CancelsParticipantTokenBeforeDisposingServices()
        {
            IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<TokenKeepingStartable>(Lifetime.Singleton);
                b.Register<TokenObservingDisposable>(Lifetime.Singleton);
            });
            TokenKeepingStartable startable = resolver.Resolve<TokenKeepingStartable>();
            TokenObservingDisposable observer = resolver.Resolve<TokenObservingDisposable>();
            resolver.RunInitializables();
            await resolver.RunAsyncStartablesAsync(CancellationToken.None);
            observer.Token = startable.ReceivedToken;

            Exception caught = await TaskCapture.CatchAsync(resolver.ShutdownAsync(CancellationToken.None));

            Assert.IsNull(caught, "Shutdown completes");
            Assert.IsTrue(observer.TokenCanceledAtDispose, "The participant token is cancelled before services are disposed");
        }

        /// <summary>
        /// When the running participant completes on a worker thread, shutdown still disposes the scope on the
        /// caller's synchronization context.
        /// </summary>
        [Test]
        public void ShutdownAsync_WhenParticipantCompletesOnWorkerThread_DisposesOnCallerContext()
        {
            GatedStartable gated = new GatedStartable();
            ContextRecordingDisposable recorder = new ContextRecordingDisposable();
            IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton);
                b.RegisterFactory<ContextRecordingDisposable>(_ => recorder, Lifetime.Singleton);
            });
            resolver.Resolve<ContextRecordingDisposable>();
            resolver.RunInitializables();
            SingleThreadedSynchronizationContext context = new SingleThreadedSynchronizationContext();
            Exception shutdownFailure = null;

            context.Run(async () =>
            {
                Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);
                Task shutdown = resolver.ShutdownAsync(CancellationToken.None);
                await Task.Run(() => gated.Gate.SetResult(true));
                _ = await TaskCapture.CatchAsync(startup);
                shutdownFailure = await TaskCapture.CatchAsync(shutdown);
            }, TimeSpan.FromSeconds(10));

            Assert.IsNull(shutdownFailure, "Shutdown completes");
            Assert.AreSame(context, recorder.RecordedContext, "Disposal runs on the caller's synchronization context");
            Assert.AreEqual(context.ThreadID, recorder.RecordedThreadID, "Disposal runs on the caller's thread");
        }

        /// <summary>
        /// Shutting a child down leaves its parent ready, resolving, and owning its own services.
        /// </summary>
        [Test]
        public async Task ShutdownAsync_WhenCalledOnChild_LeavesParentReady()
        {
            LoggedDisposable rootService = new LoggedDisposable("root");
            using IObjectResolver root = Container.Build(b => b.RegisterFactory<LoggedDisposable>(_ => rootService, Lifetime.Singleton));
            root.Resolve<LoggedDisposable>();
            root.RunInitializables();
            await root.RunAsyncStartablesAsync(CancellationToken.None);
            IObjectResolver child = root.CreateChildScope(_ => { });
            child.RunInitializables();
            await child.RunAsyncStartablesAsync(CancellationToken.None);

            Exception caught = await TaskCapture.CatchAsync(child.ShutdownAsync(CancellationToken.None));

            Assert.IsNull(caught, "The child's shutdown completes");
            Assert.AreEqual(LifecycleState.Ready, ((Scope)root).State, "The parent stays ready");
            Assert.AreSame(rootService, root.Resolve<LoggedDisposable>(), "The parent keeps resolving");
            Assert.AreEqual(0, rootService.DisposeCount, "The child's disposal leaves the parent's services alone");
        }

        /// <summary>
        /// A token callback that disposes the scope while shutdown cancels it does the disposal itself; shutdown
        /// still completes, once that disposal finished and the participant returned, without reporting a failure.
        /// </summary>
        [Test]
        public async Task ShutdownAsync_WhenTokenCallbackDisposesScope_Completes()
        {
            LoggedDisposable service = new LoggedDisposable("svc");
            IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<DisposingCallbackStartable>(Lifetime.Singleton);
                b.RegisterFactory<LoggedDisposable>(_ => service, Lifetime.Singleton);
            });
            DisposingCallbackStartable.Target = resolver;
            DisposingCallbackStartable startable = resolver.Resolve<DisposingCallbackStartable>();
            resolver.Resolve<LoggedDisposable>();
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Task shutdown = resolver.ShutdownAsync(CancellationToken.None);
            startable.Gate.SetResult(true);
            Exception caught = await TaskCapture.CatchAsync(shutdown);
            _ = await TaskCapture.CatchAsync(startup);

            Assert.IsNull(caught, "Shutdown completes without a failure");
            Assert.AreEqual(1, service.DisposeCount, "The scope is disposed once");
            Assert.AreEqual(LifecycleState.Disposed, ((Scope)resolver).State, "The scope is disposed");
        }

        /// <summary>
        /// A token already cancelled when shutdown is called means no wait at all: the scope is disposed during the
        /// call and the returned task is already cancelled.
        /// </summary>
        [Test]
        public async Task ShutdownAsync_WhenTokenAlreadyCanceled_DisposesAtOnceAndCompletesAsCanceled()
        {
            GatedStartable gated = new GatedStartable();
            using CancellationTokenSource source = new CancellationTokenSource();
            source.Cancel();
            IObjectResolver resolver = Container.Build(b => b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton));
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Task shutdown = resolver.ShutdownAsync(source.Token);
            bool completedAtReturn = shutdown.IsCompleted;
            bool canceledAtReturn = shutdown.IsCanceled;
            LifecycleState state = ((Scope)resolver).State;
            gated.Gate.SetResult(true);
            _ = await TaskCapture.CatchAsync(startup);
            _ = await TaskCapture.CatchAsync(shutdown);

            Assert.IsTrue(completedAtReturn, "Shutdown does not wait at all");
            Assert.IsTrue(canceledAtReturn, "Shutdown completes as cancelled");
            Assert.AreEqual(LifecycleState.Disposed, state, "The scope is disposed during the call");
        }

        /// <summary>
        /// A child created on a scope while it waits in shutdown cannot run its pass, and is disposed with the scope.
        /// </summary>
        [Test]
        public async Task ShutdownAsync_WhenChildCreatedWhileWaiting_DisposesItWithScope()
        {
            GatedStartable gated = new GatedStartable();
            LoggedDisposable childService = new LoggedDisposable("child");
            IObjectResolver resolver = Container.Build(b => b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton));
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);
            Task shutdown = resolver.ShutdownAsync(CancellationToken.None);

            IObjectResolver child = resolver.CreateChildScope(b => b.RegisterFactory<LoggedDisposable>(_ => childService, Lifetime.Scoped));
            child.Resolve<LoggedDisposable>();
            Exception passFailure = Record(() => child.RunInitializables());
            gated.Gate.SetResult(true);
            _ = await TaskCapture.CatchAsync(shutdown);
            _ = await TaskCapture.CatchAsync(startup);

            Assert.IsInstanceOf<InvalidOperationException>(passFailure, "A child of a stopping scope cannot run its pass");
            Assert.Throws<ObjectDisposedException>(() => child.Resolve<LoggedDisposable>(), "The child is disposed with the scope");
            Assert.AreEqual(1, childService.DisposeCount, "The child's disposable is disposed once");
        }

        /// <summary>
        /// Runs <paramref name="action"/> and returns the exception it threw, or null when it completed.
        /// </summary>
        /// <param name="action">The call to run.</param>
        /// <returns>The exception, or null.</returns>
        private Exception Record(Action action)
        {
            try
            {
                action.Invoke();
            }
            catch (Exception exception)
            {
                return exception;
            }

            return null;
        }
    }
}
