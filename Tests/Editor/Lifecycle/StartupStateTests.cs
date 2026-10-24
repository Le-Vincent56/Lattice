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
    /// Verifies the state rules of <c>RunAsyncStartablesAsync</c>: startup requires a completed pass and, on a child,
    /// a ready parent; it runs once per scope and leaves the scope ready; a concurrent or re-entrant call is refused;
    /// once startup has begun the initialization pass initializes nothing (a no-op, or a refusal naming a pending
    /// object) and supplied objects still initialize.
    ///
    /// Exercises <c>Scope.BeginStart</c>, <c>Scope.CompleteStart</c> and <c>Scope.BeginPass</c>.
    /// </summary>
    [TestFixture]
    public sealed class StartupStateTests
    {
        /// <summary>
        /// Resets the static fixture state the tests read.
        /// </summary>
        [SetUp]
        public void Reset()
        {
            CountingStartable.StartCount = 0;
            LifecycleEventLog.Events.Clear();
        }

        /// <summary>
        /// A startup whose participants all start leaves the scope ready.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenAllParticipantsStart_ScopeIsReady()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IAsyncStartable, CountingStartable>(Lifetime.Singleton));
            resolver.RunInitializables();

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Assert.AreEqual(LifecycleState.Ready, ((Scope)resolver).State, "A completed startup makes the scope ready");
        }

        /// <summary>
        /// Startup before the scope's initialization pass is refused and starts nothing.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenScopeNotInitialized_ThrowsInvalidOperationException()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IAsyncStartable, CountingStartable>(Lifetime.Singleton));

            Exception caught = await TaskCapture.CatchAsync(resolver.RunAsyncStartablesAsync(CancellationToken.None));

            Assert.IsInstanceOf<InvalidOperationException>(caught, "Startup requires the pass");
            Assert.AreEqual(0, CountingStartable.StartCount, "Nothing starts");
        }

        /// <summary>
        /// A second call after a successful startup starts nothing.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenCalledTwice_StartsEachParticipantOnce()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IAsyncStartable, CountingStartable>(Lifetime.Singleton));
            resolver.RunInitializables();
            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Assert.AreEqual(1, CountingStartable.StartCount, "Startup runs once per scope");
        }

        /// <summary>
        /// A call while startup runs is refused at once; the running startup's participant starts once.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenCalledWhileStartupRunning_ThrowsInvalidOperationException()
        {
            GatedStartable gated = new GatedStartable();
            using IObjectResolver resolver = Container.Build(b => b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton));
            resolver.RunInitializables();
            Task first = resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Task second = resolver.RunAsyncStartablesAsync(CancellationToken.None);
            bool refusedAtOnce = second.IsFaulted;
            gated.Gate.SetResult(true);
            await first;
            Exception refusal = await TaskCapture.CatchAsync(second);

            Assert.IsTrue(refusedAtOnce, "A call while startup runs is refused at once");
            Assert.IsInstanceOf<InvalidOperationException>(refusal, "The refusal is an InvalidOperationException");
            Assert.AreEqual(1, gated.StartCount, "The participant starts once");
        }

        /// <summary>
        /// A startup started from inside a participant's <c>StartAsync</c> is refused; the outer startup completes.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenCalledFromInsideStartAsync_ThrowsInvalidOperationException()
        {
            using IObjectResolver resolver = Container.Build(b => b.RegisterFactory<ReentrantStartable>(r => new ReentrantStartable(r), Lifetime.Singleton));
            resolver.RunInitializables();

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            ReentrantStartable participant = resolver.Resolve<ReentrantStartable>();
            Assert.IsInstanceOf<InvalidOperationException>(participant.CaughtException, "A re-entrant startup is refused");
            Assert.AreEqual(1, participant.StartCount, "The participant starts once");
        }

        /// <summary>
        /// A child's startup before its parent's startup completed is refused.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenCalledOnChildBeforeParentReady_ThrowsInvalidOperationException()
        {
            using IObjectResolver root = Container.Build(b => b.Register<IAsyncStartable, CountingStartable>(Lifetime.Singleton));
            root.RunInitializables();
            using IObjectResolver child = root.CreateChildScope(b => b.RegisterFactory<IAsyncStartable>(_ => new OrderedStartable("child"), Lifetime.Scoped));
            child.RunInitializables();

            Exception caught = await TaskCapture.CatchAsync(child.RunAsyncStartablesAsync(CancellationToken.None));

            Assert.IsInstanceOf<InvalidOperationException>(caught, "A child starts after its parent");
            CollectionAssert.DoesNotContain(LifecycleEventLog.Events, "child:start", "The child's participant does not start");
        }

        /// <summary>
        /// After the parent's startup, a child's pass and startup run and cover the child's own participants only.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenCalledOnChildAfterParentReady_StartsOnlyChildOwnedParticipants()
        {
            using IObjectResolver root = Container.Build(b => b.RegisterFactory<IAsyncStartable>(_ => new OrderedStartable("root"), Lifetime.Singleton));
            root.RunInitializables();
            await root.RunAsyncStartablesAsync(CancellationToken.None);
            using IObjectResolver child = root.CreateChildScope(b => b.RegisterFactory<IAsyncStartable>(_ => new OrderedStartable("child"), Lifetime.Scoped));
            child.RunInitializables();

            await child.RunAsyncStartablesAsync(CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "root:start", "child:start" }, LifecycleEventLog.Events, "Each scope starts its own participants once");
        }

        /// <summary>
        /// Once startup has begun, the pass initializes nothing: an <see cref="IInitializable"/> created since is
        /// reported instead of initialized, so the caller initializes it with <c>InitializeInstance</c>.
        /// </summary>
        [Test]
        public async Task RunInitializables_WhenScopeReadyAndUninitializedObjectCached_ThrowsInvalidOperationException()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IAsyncStartable, CountingStartable>(Lifetime.Singleton);
                b.RegisterFactory<IServiceA>(_ => new InitializableServiceA(), Lifetime.Singleton);
            });
            resolver.RunInitializables();
            await resolver.RunAsyncStartablesAsync(CancellationToken.None);
            InitializableServiceA late = (InitializableServiceA)resolver.Resolve<IServiceA>();

            Assert.Throws<InvalidOperationException>(() => resolver.RunInitializables(), "A pending object is reported after startup");
            Assert.AreEqual(0, late.InitializeCount, "The pass initializes nothing after startup");
        }

        /// <summary>
        /// Once startup has begun, a pass with nothing pending does nothing, so a caller that runs the pass again, such
        /// as a scene host handed a started scope, keeps working.
        /// </summary>
        [Test]
        public async Task RunInitializables_WhenScopeReadyAndNothingPending_DoesNothing()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IAsyncStartable, CountingStartable>(Lifetime.Singleton));
            resolver.RunInitializables();
            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Assert.DoesNotThrow(() => resolver.RunInitializables(), "A pass with nothing pending is a no-op after startup");
        }

        /// <summary>
        /// A ready scope still initializes a supplied object.
        /// </summary>
        [Test]
        public async Task InitializeInstance_WhenScopeReady_InitializesIt()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IAsyncStartable, CountingStartable>(Lifetime.Singleton));
            resolver.RunInitializables();
            await resolver.RunAsyncStartablesAsync(CancellationToken.None);
            InitTracking supplied = new InitTracking();

            bool ran = resolver.InitializeInstance(supplied);

            Assert.IsTrue(ran, "InitializeInstance runs on a ready scope");
            Assert.AreEqual(1, supplied.InitializeCallCount, "The supplied object is initialized once");
        }
    }
}
