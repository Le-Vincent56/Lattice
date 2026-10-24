using System;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Verifies who <c>RunAsyncStartablesAsync</c> starts and in what order: every <see cref="IAsyncStartable"/> the
    /// initialization pass built or the scope has cached, selected by implementation or decorator type, once each by
    /// reference, sequentially in creation order across service types; never a Transient or a supplied instance; never
    /// an <see cref="IInitializable"/> that has not been initialized.
    ///
    /// Exercises <c>Scope.MaterializeParticipants</c> and <c>Scope.CollectStartTargets</c>.
    /// </summary>
    [TestFixture]
    public sealed class AsyncStartableTests
    {
        /// <summary>
        /// Resets the static fixture state the tests read; every fixture counter and log here is static.
        /// </summary>
        [SetUp]
        public void Reset()
        {
            AsyncStartLogger.Order.Clear();
            ValueEqualStartable.StartCount = 0;
            CountingStartable.StartCount = 0;
            LifecycleEventLog.Events.Clear();
        }

        /// <summary>
        /// Multiple <see cref="IAsyncStartable"/> registrations must run sequentially; each one's
        /// <c>StartAsync</c> is fully awaited before the next is invoked. The <c>Task.Yield()</c> inside
        /// <see cref="AsyncStartLogger.StartAsync"/> would interleave enter/exit entries if the runner accidentally ran
        /// them in parallel.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenMultipleRegistered_RunsInRegistrationOrder()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => new AsyncStartLogger("A"), Lifetime.Singleton);
                b.RegisterFactory<IAsyncStartable>(_ => new AsyncStartLogger("B"), Lifetime.Singleton);
                b.RegisterFactory<IAsyncStartable>(_ => new AsyncStartLogger("C"), Lifetime.Singleton);
            });
            resolver.RunInitializables();

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            // Sequential: each enter is followed by its own exit before the next enter.
            // Any parallelism would produce interleaved entries (e.g., A:enter, B:enter, ...).
            Assert.That(
                AsyncStartLogger.Order,
                Is.EqualTo(new[]
                {
                    "A:enter", "A:exit",
                    "B:enter", "B:exit",
                    "C:enter", "C:exit"
                })
            );
        }

        /// <summary>
        /// Two registrations whose instances are equal by <c>Equals</c> are still two objects, and each is started.
        /// Guards the once-per-call rule against comparing participants by value instead of by reference.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenDistinctStartablesAreEqual_StartsBoth()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => new ValueEqualStartable(), Lifetime.Singleton);
                b.RegisterFactory<IAsyncStartable>(_ => new ValueEqualStartable(), Lifetime.Singleton);
            });
            resolver.RunInitializables();

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Assert.AreEqual(2, ValueEqualStartable.StartCount, "Distinct instances are started even when they compare equal");
        }

        /// <summary>
        /// A startable registered under an interface that does not extend <see cref="IAsyncStartable"/> is selected by
        /// its implementation type and started.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenStartableRegisteredUnderUnrelatedInterface_StartsIt()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IStartupProbe, ProbeStartable>(Lifetime.Singleton));
            resolver.RunInitializables();

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            CollectionAssert.Contains(LifecycleEventLog.Events, "probe:start", "A startable under an unrelated interface starts");
        }

        /// <summary>
        /// The initialization pass builds a startable nobody resolved, and does not start it.
        /// </summary>
        [Test]
        public void RunInitializables_WhenStartableRegisteredUnderUnrelatedInterface_ConstructsIt()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IStartupProbe, ProbeStartable>(Lifetime.Singleton));

            resolver.RunInitializables();

            CollectionAssert.AreEqual(new[] { "probe:ctor" }, LifecycleEventLog.Events, "The pass builds the startable and does not start it");
        }

        /// <summary>
        /// A Transient startable is never cached, so startup never starts it.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenStartableIsTransient_DoesNotStartIt()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IAsyncStartable, CountingStartable>(Lifetime.Transient));
            resolver.RunInitializables();

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Assert.AreEqual(0, CountingStartable.StartCount, "A Transient startable is not started");
        }

        /// <summary>
        /// A supplied instance is caller-owned: the container does not start it.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenInstanceRegistered_DoesNotStartIt()
        {
            using IObjectResolver resolver = Container.Build(b => b.RegisterInstance<IAsyncStartable>(new CountingStartable()));
            resolver.RunInitializables();

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Assert.AreEqual(0, CountingStartable.StartCount, "A RegisterInstance value is not started");
        }

        /// <summary>
        /// A startable decorator makes its registration a participant: the pass builds it and startup starts the
        /// decorator.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenOnlyDecoratorIsStartable_StartsDecorator()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<ILoggable, RealLoggable>(Lifetime.Singleton);
                b.RegisterDecorator<ILoggable, StartableLoggableDecorator>(Lifetime.Singleton);
            });
            resolver.RunInitializables();

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            CollectionAssert.Contains(LifecycleEventLog.Events, "decorator:start", "A startable decorator is started");
        }

        /// <summary>
        /// A factory whose service type hides the hook takes part once its result is cached: startup starts every
        /// <see cref="IAsyncStartable"/> the scope holds.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenOpaqueFactoryResultCachedBeforeStartup_StartsIt()
        {
            using IObjectResolver resolver = Container.Build(b => b.RegisterFactory<IStartupProbe>(_ => new ProbeStartable(), Lifetime.Singleton));
            _ = resolver.Resolve<IStartupProbe>();
            resolver.RunInitializables();

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            CollectionAssert.Contains(LifecycleEventLog.Events, "probe:start", "A cached opaque factory result starts");
        }

        /// <summary>
        /// A cached participant that is also <see cref="IInitializable"/> but was created after the pass is never
        /// started uninitialized: startup refuses before anything starts.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenCachedStartableIsUninitialized_ThrowsInvalidOperationException()
        {
            using IObjectResolver resolver = Container.Build(b => b.RegisterFactory<IStartupProbe>(_ => new InitializingProbeStartable(), Lifetime.Singleton));
            resolver.RunInitializables();
            _ = resolver.Resolve<IStartupProbe>();

            Exception caught = await TaskCapture.CatchAsync(resolver.RunAsyncStartablesAsync(CancellationToken.None));

            Assert.IsInstanceOf<InvalidOperationException>(caught, "Startup refuses an uninitialized participant");
            CollectionAssert.DoesNotContain(LifecycleEventLog.Events, "both:start", "Nothing starts");
        }

        /// <summary>
        /// A participant in both hooks is initialized by the pass before startup starts it.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenStartableAlsoInitializable_InitializesBeforeStart()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IStartupProbe, InitializingProbeStartable>(Lifetime.Singleton));
            resolver.RunInitializables();

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "both:init", "both:start" }, LifecycleEventLog.Events, "Initialized, then started");
        }

        /// <summary>
        /// Startables registered under different service types start in registration order, not grouped by
        /// service type.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenStartablesInterleavedAcrossServiceTypes_StartsInRegistrationOrder()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => new OrderedStartable("A"), Lifetime.Singleton);
                b.RegisterFactory<IOrderedStartable>(_ => new OrderedStartable("B"), Lifetime.Singleton);
                b.RegisterFactory<IAsyncStartable>(_ => new OrderedStartable("C"), Lifetime.Singleton);
            });
            resolver.RunInitializables();

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "A:start", "B:start", "C:start" }, LifecycleEventLog.Events, "Registration order across service types");
        }

        /// <summary>
        /// A dependency starts before the startable built with it, even when the dependent was registered first.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenDependentRegisteredBeforeDependency_StartsDependencyFirst()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<LoggedStartableDependent>(Lifetime.Singleton);
                b.Register<LoggedStartableLeaf>(Lifetime.Singleton);
            });
            resolver.RunInitializables();

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "leaf:start", "dependent:start" }, LifecycleEventLog.Events, "Creation order: the dependency first");
        }
    }
}