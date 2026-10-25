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
    /// Verifies cooperative cancellation of startup: the token is checked before and between participants; a token
    /// cancelled before the call changes nothing; cancellation after startup began stops further participants and
    /// faults the scope; Lattice waits for a running participant; <c>Dispose</c> cancels the participants' token,
    /// returns without waiting, and keeps the scope from becoming ready.
    ///
    /// Exercises <c>Scope.BeginStart</c>, <c>Scope.ThrowIfStartupStopped</c>, <c>Scope.CompleteStart</c> and the head of
    /// <c>Scope.Dispose</c>.
    /// </summary>
    [TestFixture]
    public sealed class StartupCancellationTests
    {
        /// <summary>
        /// Resets the static fixture state the tests read.
        /// </summary>
        [SetUp]
        public void Reset()
        {
            CountingStartable.StartCount = 0;
        }

        /// <summary>
        /// A token cancelled before the call starts nothing and leaves the scope startable.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenTokenCanceledBeforeCall_StartsNothingAndLaterCallStarts()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IAsyncStartable, CountingStartable>(Lifetime.Singleton));
            resolver.RunInitializables();
            using CancellationTokenSource source = new CancellationTokenSource();
            source.Cancel();

            Exception canceled = await TaskCapture.CatchAsync(resolver.RunAsyncStartablesAsync(source.Token));
            int startedByCanceledCall = CountingStartable.StartCount;
            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Assert.AreEqual(0, startedByCanceledCall, "A cancelled call starts nothing");
            Assert.IsInstanceOf<OperationCanceledException>(canceled, "The cancelled call completes as cancelled");
            Assert.AreEqual(1, CountingStartable.StartCount, "The scope can still be started");
        }

        /// <summary>
        /// Cancellation that arrives while one participant starts keeps the next from starting.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenTokenCanceledBetweenParticipants_DoesNotStartLaterParticipants()
        {
            using CancellationTokenSource source = new CancellationTokenSource();
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => new CancelingStartable(source), Lifetime.Singleton);
                b.Register<CountingStartable>(Lifetime.Singleton);
            });
            resolver.RunInitializables();

            Exception caught = await TaskCapture.CatchAsync(resolver.RunAsyncStartablesAsync(source.Token));

            Assert.AreEqual(0, CountingStartable.StartCount, "No participant starts after cancellation");
            Assert.IsInstanceOf<OperationCanceledException>(caught, "Startup completes as cancelled");
        }

        /// <summary>
        /// A startup cancelled after a participant started faults the scope, which refuses a later startup.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenCanceledAfterParticipantStarted_ScopeRefusesLaterStartup()
        {
            using CancellationTokenSource source = new CancellationTokenSource();
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => new CancelingStartable(source), Lifetime.Singleton);
                b.Register<CountingStartable>(Lifetime.Singleton);
            });
            resolver.RunInitializables();
            _ = await TaskCapture.CatchAsync(resolver.RunAsyncStartablesAsync(source.Token));

            Exception repeat = await TaskCapture.CatchAsync(resolver.RunAsyncStartablesAsync(CancellationToken.None));

            Assert.IsInstanceOf<InvalidOperationException>(repeat, "A cancelled startup faults the scope");
        }

        /// <summary>
        /// Lattice waits for a participant that ignores cancellation, then starts no later participant.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenParticipantIgnoresCancellation_AwaitsItAndStartsNoLaterParticipant()
        {
            GatedStartable gated = new GatedStartable();
            using CancellationTokenSource source = new CancellationTokenSource();
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton);
                b.Register<CountingStartable>(Lifetime.Singleton);
            });
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(source.Token);

            source.Cancel();
            bool completedBeforeParticipant = startup.IsCompleted;
            gated.Gate.SetResult(true);
            Exception caught = await TaskCapture.CatchAsync(startup);

            Assert.IsFalse(completedBeforeParticipant, "Startup waits for the running participant");
            Assert.AreEqual(0, CountingStartable.StartCount, "No participant starts after cancellation");
            Assert.IsInstanceOf<OperationCanceledException>(caught, "Startup completes as cancelled");
        }

        /// <summary>
        /// Disposing a scope during startup cancels the token its participants received and keeps later
        /// participants from starting.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenScopeDisposedDuringStartup_CancelsParticipantTokenAndStartsNoLaterParticipant()
        {
            GatedStartable gated = new GatedStartable();
            IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton);
                b.Register<CountingStartable>(Lifetime.Singleton);
            });
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);

            resolver.Dispose();
            bool tokenCanceled = gated.ReceivedToken.IsCancellationRequested;
            gated.Gate.SetResult(true);
            Exception caught = await TaskCapture.CatchAsync(startup);

            Assert.IsTrue(tokenCanceled, "Dispose cancels the participants' token");
            Assert.AreEqual(0, CountingStartable.StartCount, "No participant starts after Dispose");
            Assert.IsInstanceOf<OperationCanceledException>(caught, "Startup completes as cancelled");
        }

        /// <summary>
        /// A scope disposed while its last participant runs never becomes ready, even when that participant ignores
        /// the token and completes.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenScopeDisposedDuringLastParticipant_DoesNotBecomeReady()
        {
            GatedStartable gated = new GatedStartable();
            IObjectResolver resolver = Container.Build(b => b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton));
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);

            resolver.Dispose();
            gated.Gate.SetResult(true);
            Exception caught = await TaskCapture.CatchAsync(startup);

            Assert.AreEqual(LifecycleState.Disposed, ((Scope)resolver).State, "A scope disposed during startup never becomes ready");
            Assert.IsInstanceOf<OperationCanceledException>(caught, "Startup completes as cancelled");
        }

        /// <summary>
        /// <c>Dispose</c> cancels startup and returns without waiting for the running participant.
        /// </summary>
        [Test]
        public async Task Dispose_WhenStartupRunning_ReturnsBeforeParticipantCompletes()
        {
            GatedStartable gated = new GatedStartable();
            IObjectResolver resolver = Container.Build(b => b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton));
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);

            resolver.Dispose();
            bool completedWhenDisposeReturned = startup.IsCompleted;
            gated.Gate.SetResult(true);
            _ = await TaskCapture.CatchAsync(startup);

            Assert.IsFalse(completedWhenDisposeReturned, "Dispose does not wait for startup");
        }

        /// <summary>
        /// Disposing a parent during a child's startup reaches the child's participants through the cascade.
        /// </summary>
        [Test]
        public async Task Dispose_WhenParentDisposedDuringChildStartup_CancelsChildParticipantToken()
        {
            GatedStartable gated = new GatedStartable();
            IObjectResolver root = Container.Build(_ => { });
            root.RunInitializables();
            await root.RunAsyncStartablesAsync(CancellationToken.None);
            IObjectResolver child = root.CreateChildScope(b => b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Scoped));
            child.RunInitializables();
            Task startup = child.RunAsyncStartablesAsync(CancellationToken.None);

            root.Dispose();
            bool tokenCanceled = gated.ReceivedToken.IsCancellationRequested;
            gated.Gate.SetResult(true);
            Exception caught = await TaskCapture.CatchAsync(startup);

            Assert.IsTrue(tokenCanceled, "The cascade cancels the child's startup");
            Assert.IsInstanceOf<OperationCanceledException>(caught, "The child's startup completes as cancelled");
        }
    }
}
