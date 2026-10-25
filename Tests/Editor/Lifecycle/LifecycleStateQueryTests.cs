using System;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Exceptions;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Pins the public lifecycle state query, one state per test: <c>GetLifecycleState</c> reports created,
    /// initializing, initialized, starting, ready, faulted, stopping and disposed as the scope moves through its
    /// lifecycle, a disposed scope included, and refuses a resolver that is not the built-in scope; the public enum
    /// lists its states in lifecycle order. Imports only the public namespace, so it proves the enum is public.
    ///
    /// Exercises <c>ResolverLifecycleExtensions.GetLifecycleState</c> and <c>Scope.State</c>.
    /// </summary>
    [TestFixture]
    public sealed class LifecycleStateQueryTests
    {
        /// <summary>
        /// Resets the static fixture state the tests read.
        /// </summary>
        [SetUp]
        public void Reset()
        {
            CountingStartable.StartCount = 0;
            StateRecordingInitializable.Target = null;
            StateRecordingInitializable.RecordedState = LifecycleState.Created;
        }

        /// <summary>
        /// A freshly built scope reports created.
        /// </summary>
        [Test]
        public void GetLifecycleState_WhenScopeBuilt_ReturnsCreated()
        {
            using IObjectResolver resolver = Container.Build(_ => { });

            LifecycleState state = resolver.GetLifecycleState();

            Assert.AreEqual(LifecycleState.Created, state, "A built scope is created");
        }

        /// <summary>
        /// Read from inside a participant's <c>Initialize</c>, the state is initializing.
        /// </summary>
        [Test]
        public void GetLifecycleState_WhenCalledDuringPass_ReturnsInitializing()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<StateRecordingInitializable>(Lifetime.Singleton));
            StateRecordingInitializable.Target = resolver;

            resolver.RunInitializables();

            Assert.AreEqual(LifecycleState.Initializing, StateRecordingInitializable.RecordedState, "The pass reports initializing while it runs");
        }

        /// <summary>
        /// After a successful pass the state is initialized.
        /// </summary>
        [Test]
        public void GetLifecycleState_WhenPassCompleted_ReturnsInitialized()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<InitTracking>(Lifetime.Singleton));

            resolver.RunInitializables();

            Assert.AreEqual(LifecycleState.Initialized, resolver.GetLifecycleState(), "A completed pass leaves the scope initialized");
        }

        /// <summary>
        /// While a participant's start is still running, the state is starting.
        /// </summary>
        [Test]
        public async Task GetLifecycleState_WhenStartupRunning_ReturnsStarting()
        {
            GatedStartable gated = new GatedStartable();
            using IObjectResolver resolver = Container.Build(b => b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton));
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);

            LifecycleState state = resolver.GetLifecycleState();
            gated.Gate.SetResult(true);
            await startup;

            Assert.AreEqual(LifecycleState.Starting, state, "A running startup reports starting");
        }

        /// <summary>
        /// After a completed startup the state is ready.
        /// </summary>
        [Test]
        public async Task GetLifecycleState_WhenStartupCompleted_ReturnsReady()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IAsyncStartable, CountingStartable>(Lifetime.Singleton));
            resolver.RunInitializables();

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Assert.AreEqual(LifecycleState.Ready, resolver.GetLifecycleState(), "A completed startup leaves the scope ready");
        }

        /// <summary>
        /// A startup cancelled after it began reports faulted, never ready.
        /// </summary>
        [Test]
        public async Task GetLifecycleState_WhenStartupCanceledAfterItBegan_ReturnsFaulted()
        {
            using CancellationTokenSource source = new CancellationTokenSource();
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => new CancelingStartable(source), Lifetime.Singleton);
                b.RegisterFactory<IAsyncStartable>(_ => new CountingStartable(), Lifetime.Singleton);
            });
            resolver.RunInitializables();

            Exception caught = await TaskCapture.CatchAsync(resolver.RunAsyncStartablesAsync(source.Token));

            LifecycleState state = resolver.GetLifecycleState();
            Assert.IsInstanceOf<OperationCanceledException>(caught, "The startup is cancelled");
            Assert.AreEqual(LifecycleState.Faulted, state, "A startup cancelled after it began faults the scope");
            Assert.AreNotEqual(LifecycleState.Ready, state, "A cancelled startup never reports ready");
        }

        /// <summary>
        /// A pass whose participant throws reports faulted.
        /// </summary>
        [Test]
        public void GetLifecycleState_WhenPassFailed_ReturnsFaulted()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<ThrowingInitializable>(Lifetime.Singleton));

            Assert.Throws<LifecycleException>(() => resolver.RunInitializables(), "The pass fails");

            Assert.AreEqual(LifecycleState.Faulted, resolver.GetLifecycleState(), "A failed pass faults the scope");
        }

        /// <summary>
        /// While a shutdown waits for a running startup, the state is stopping.
        /// </summary>
        [Test]
        public async Task GetLifecycleState_WhenShutdownWaitsForStartup_ReturnsStopping()
        {
            GatedStartable gated = new GatedStartable();
            using IObjectResolver resolver = Container.Build(b => b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton));
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);
            Task shutdown = resolver.ShutdownAsync(CancellationToken.None);

            LifecycleState state = resolver.GetLifecycleState();
            gated.Gate.SetResult(true);
            _ = await TaskCapture.CatchAsync(startup);
            await shutdown;

            Assert.AreEqual(LifecycleState.Stopping, state, "A shutdown waiting for startup reports stopping");
        }

        /// <summary>
        /// A disposed scope reports disposed, and reading it does not throw.
        /// </summary>
        [Test]
        public void GetLifecycleState_WhenDisposed_ReturnsDisposed()
        {
            IObjectResolver resolver = Container.Build(_ => { });
            resolver.Dispose();
            LifecycleState state = LifecycleState.Created;

            Assert.DoesNotThrow(() => state = resolver.GetLifecycleState(), "Reading a disposed scope's state does not throw");

            Assert.AreEqual(LifecycleState.Disposed, state, "A disposed scope reports disposed");
        }

        /// <summary>
        /// A resolver that is not the built-in scope is refused.
        /// </summary>
        [Test]
        public void GetLifecycleState_WhenResolverIsNotBuiltInScope_ThrowsInvalidOperationException()
        {
            IObjectResolver resolver = new ForeignResolver();

            InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => resolver.GetLifecycleState(), "A foreign resolver is refused");

            Assert.AreEqual("GetLifecycleState is only supported on the built-in Scope resolver.", refusal.Message, "The refusal names the built-in resolver");
        }

        /// <summary>
        /// The public enum lists its states in lifecycle order.
        /// </summary>
        [Test]
        public void LifecycleState_WhenEnumerated_ListsStatesInLifecycleOrder()
        {
            string[] names = Enum.GetNames(typeof(LifecycleState));

            CollectionAssert.AreEqual(
                new[] { "Created", "Initializing", "Initialized", "Starting", "Ready", "Faulted", "Stopping", "Disposed" },
                names,
                "The states are listed in lifecycle order"
            );
        }
    }
}
