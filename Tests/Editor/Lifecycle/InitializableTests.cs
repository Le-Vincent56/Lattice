using System;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Verifies the <see cref="IInitializable"/> contract of <c>RunInitializables</c>: the pass builds every
    /// Singleton or Scoped registration of its scope whose implementation or decorator type implements
    /// <see cref="IInitializable"/>, initializes each instance once (by reference) in creation order after every
    /// participant is constructed, covers only the calling scope's registrations after the parent's pass, refuses a
    /// re-entrant pass, and leaves Transients and supplied instances alone.
    ///
    /// Exercises <c>Scope.RunInitializables</c>, its materialization walk and the <c>_initialized</c> tracking set.
    /// </summary>
    [TestFixture]
    public sealed class InitializableTests
    {
        /// <summary>
        /// Resets the shared event log and the static counter on <see cref="ValueEqualInitializable"/>, which the
        /// order and identity tests share.
        /// </summary>
        [SetUp]
        public void Reset()
        {
            LifecycleEventLog.Events.Clear();
            ValueEqualInitializable.InitializeCount = 0;
        }

        /// <summary>
        /// <c>RunInitializables</c> must be idempotent; calling it twice on the same resolver
        /// must not re-run <c>Initialize</c> on already-initialized instances.
        /// Verifies the <c>HashSet&lt;object&gt; _initialized</c> deduplication path.
        /// </summary>
        [Test]
        public void RunInitializables_WhenInstanceImplementsIInitializable_CallsInitializeOnce()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<InitTracking>(Lifetime.Singleton));
            InitTracking instance = resolver.Resolve<InitTracking>();

            // Call twice; second call must be a no-op for already-initialized instances
            resolver.RunInitializables();
            resolver.RunInitializables();

            Assert.That(instance.InitializeCallCount, Is.EqualTo(1), "Initialize should only run once");
        }

        /// <summary>
        /// Verifies the Phase 1 (bindings) -> Phase 2 (Initialize) ordering: when
        /// <c>CrossInstanceInit</c>'s <c>Initialize</c> runs, its peer dependency (<c>InitTracking</c>) must already be constructed and
        /// bound. The peer's non-null reference inside <c>Initialize</c> proves Phase 1 completed first.
        /// </summary>
        [Test]
        public void RunInitializables_WhenInstanceDependsOnAnother_BothBoundBeforeEitherInitializes()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<InitTracking>(Lifetime.Singleton);
                b.Register<CrossInstanceInit>(Lifetime.Singleton);
            });

            // Resolve both; this constructs both (Phase 1) and parls them in the singleton cache.
            // RunInitializables then drives Phase 2 across both.
            InitTracking a = resolver.Resolve<InitTracking>();
            CrossInstanceInit b = resolver.Resolve<CrossInstanceInit>();

            resolver.RunInitializables();

            Assert.IsTrue(b.OtherWasReady, "Phase 2 sees Phase 1 bindings complete across the scope.");
        }

        /// <summary>
        /// A decorated Scoped service is two cached objects; <c>RunInitializables</c> initializes the implementation
        /// and the decorator once each.
        /// </summary>
        [Test]
        public void RunInitializables_WhenDecoratedScopedInitialized_InnerAndWrapperBothInitialized()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<ILoggable, TrackedLoggable>(Lifetime.Scoped);
                b.RegisterDecorator<ILoggable, TrackedLoggableDecorator>(Lifetime.Scoped);
            });
            TrackedLoggableDecorator decorator = (TrackedLoggableDecorator)resolver.Resolve<ILoggable>();
            TrackedLoggable inner = (TrackedLoggable)decorator.Inner;

            resolver.RunInitializables();

            Assert.AreEqual(1, decorator.InitializeCount, "The decorator is initialized once");
            Assert.AreEqual(1, inner.InitializeCount, "The decorated inner instance is initialized once");
        }

        /// <summary>
        /// The root walks its decorated Singleton cache too: a decorated Singleton's implementation and decorator are
        /// initialized once each.
        /// </summary>
        [Test]
        public void RunInitializables_WhenDecoratedSingletonInitialized_InnerAndWrapperBothInitialized()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<ILoggable, TrackedLoggable>(Lifetime.Singleton);
                b.RegisterDecorator<ILoggable, TrackedLoggableDecorator>(Lifetime.Singleton);
            });
            TrackedLoggableDecorator decorator = (TrackedLoggableDecorator)resolver.Resolve<ILoggable>();
            TrackedLoggable inner = (TrackedLoggable)decorator.Inner;

            resolver.RunInitializables();

            Assert.AreEqual(1, decorator.InitializeCount, "The Singleton decorator is initialized once");
            Assert.AreEqual(1, inner.InitializeCount, "The decorated Singleton's inner instance is initialized once");
        }

        /// <summary>
        /// The pass builds a registered participant nobody resolved: its implementation type implements
        /// <see cref="IInitializable"/>, so it is constructed and initialized without a prior <c>Resolve</c>.
        /// </summary>
        [Test]
        public void RunInitializables_WhenParticipantNotResolvedBeforePass_ConstructsAndInitializesIt()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<InitTracking>(Lifetime.Singleton));

            resolver.RunInitializables();

            InitTracking instance = resolver.Resolve<InitTracking>();
            Assert.AreEqual(1, instance.InitializeCallCount, "The pass builds and initializes a registered participant");
        }

        /// <summary>
        /// Every participant is constructed before any is initialized, and a dependency is initialized before the
        /// participant constructed with it, even when the dependent was registered first.
        /// </summary>
        [Test]
        public void RunInitializables_WhenDependencyIsUnresolvedInitializable_DependencyInitializesBeforeDependent()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<LoggedDependent>(Lifetime.Singleton);
                b.Register<LoggedLeaf>(Lifetime.Singleton);
            });

            resolver.RunInitializables();

            CollectionAssert.AreEqual(
                new[] { "leaf:ctor", "dependent:ctor", "leaf:init", "dependent:init" },
                LifecycleEventLog.Events,
                "All construction precedes all initialization, and the dependency initializes first"
            );
        }

        /// <summary>
        /// Independent participants are constructed and initialized in registration order.
        /// </summary>
        [Test]
        public void RunInitializables_WhenIndependentParticipantsRegistered_InitializesInRegistrationOrder()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<LoggedSolo>(Lifetime.Singleton);
                b.Register<LoggedLeaf>(Lifetime.Singleton);
            });

            resolver.RunInitializables();

            CollectionAssert.AreEqual(
                new[] { "solo:ctor", "leaf:ctor", "solo:init", "leaf:init" },
                LifecycleEventLog.Events,
                "Independent participants follow registration order"
            );
        }

        /// <summary>
        /// Two registrations whose instances are equal by <c>Equals</c> are still two objects, and each is
        /// initialized. Guards the once-per-instance rule against comparing by value instead of by reference.
        /// </summary>
        [Test]
        public void RunInitializables_WhenInitializablesAreEqualByValue_InitializesBoth()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<ValueEqualInitializable>(Lifetime.Singleton);
                b.Register<ValueEqualInitializable>(Lifetime.Singleton);
            });
            _ = resolver.ResolveAll<ValueEqualInitializable>();

            resolver.RunInitializables();

            Assert.AreEqual(2, ValueEqualInitializable.InitializeCount, "Distinct instances are initialized even when they compare equal");
        }

        /// <summary>
        /// Two factory registrations that return one object are one participant: the object is initialized once.
        /// </summary>
        [Test]
        public void RunInitializables_WhenTwoFactoryRegistrationsReturnOneInstance_InitializesItOnce()
        {
            InitTracking shared = new InitTracking();
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<InitTracking>(_ => shared, Lifetime.Singleton);
                b.RegisterFactory<InitTracking>(_ => shared, Lifetime.Singleton);
            });
            _ = resolver.ResolveAll<InitTracking>();

            resolver.RunInitializables();

            Assert.AreEqual(1, shared.InitializeCallCount, "One object behind two registrations is initialized once");
        }

        /// <summary>
        /// A Transient registration never participates, even when an instance was resolved before the pass.
        /// </summary>
        [Test]
        public void RunInitializables_WhenInitializableIsTransient_DoesNotInitialize()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<InitTracking>(Lifetime.Transient));
            InitTracking instance = resolver.Resolve<InitTracking>();

            resolver.RunInitializables();

            Assert.AreEqual(0, instance.InitializeCallCount, "Transients are not lifecycle participants");
        }

        /// <summary>
        /// A supplied instance is caller-owned: the pass does not initialize it.
        /// </summary>
        [Test]
        public void RunInitializables_WhenInstanceRegistered_DoesNotInitializeIt()
        {
            InitTracking supplied = new InitTracking();
            using IObjectResolver resolver = Container.Build(b => b.RegisterInstance(supplied));

            resolver.RunInitializables();

            Assert.AreEqual(0, supplied.InitializeCallCount, "RegisterInstance values are not initialized by the pass");
        }

        /// <summary>
        /// A factory whose service type does not implement <see cref="IInitializable"/> is not built by the pass;
        /// once its result exists, the next pass initializes it.
        /// </summary>
        [Test]
        public void RunInitializables_WhenOpaqueFactoryResultMaterializedAfterPass_NextPassInitializesIt()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IServiceA>(_ => new InitializableServiceA(), Lifetime.Singleton);
            });
            resolver.RunInitializables();
            InitializableServiceA created = (InitializableServiceA)resolver.Resolve<IServiceA>();
            int afterFirstPass = created.InitializeCount;

            resolver.RunInitializables();

            Assert.AreEqual(0, afterFirstPass, "A factory whose service type hides the hook is not built by the pass");
            Assert.AreEqual(1, created.InitializeCount, "Once materialized, the next pass initializes it");
        }

        /// <summary>
        /// Registering a factory under the concrete type and aliasing the interface makes the hook visible, so the
        /// pass builds and initializes the result without a prior resolve.
        /// </summary>
        [Test]
        public void RunInitializables_WhenFactoryRegisteredUnderInitializableConcreteTypeWithAlias_ParticipatesWithoutPriorResolve()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<InitializableServiceA>(_ => new InitializableServiceA(), Lifetime.Singleton)
                    .As<IServiceA>();
            });

            resolver.RunInitializables();

            InitializableServiceA created = (InitializableServiceA)resolver.Resolve<IServiceA>();
            Assert.AreEqual(1, created.InitializeCount, "The concrete-type registration makes the factory a participant");
        }

        /// <summary>
        /// A registration participates when a decorator registered for its service type implements
        /// <see cref="IInitializable"/>, even though the implementation does not.
        /// </summary>
        [Test]
        public void RunInitializables_WhenOnlyDecoratorIsInitializable_BuildsAndInitializesDecorator()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<ILoggable, RealLoggable>(Lifetime.Scoped);
                b.RegisterDecorator<ILoggable, TrackedLoggableDecorator>(Lifetime.Scoped);
            });

            resolver.RunInitializables();

            TrackedLoggableDecorator decorator = (TrackedLoggableDecorator)resolver.Resolve<ILoggable>();
            Assert.AreEqual(1, decorator.InitializeCount, "An initializable decorator type makes the registration a participant");
        }

        /// <summary>
        /// A decorated participant the pass builds is initialized inner first, then its decorator.
        /// </summary>
        [Test]
        public void RunInitializables_WhenDecoratedParticipantUnresolved_InnerInitializesBeforeWrapper()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<ILoggable, LoggedLoggable>(Lifetime.Scoped);
                b.RegisterDecorator<ILoggable, LoggedLoggableDecorator>(Lifetime.Scoped);
            });

            resolver.RunInitializables();

            CollectionAssert.AreEqual(
                new[] { "inner:init", "wrapper:init" },
                LifecycleEventLog.Events,
                "The decorated instance initializes before its decorator"
            );
        }

        /// <summary>
        /// A child's pass requires the parent to have completed its own pass.
        /// </summary>
        [Test]
        public void RunInitializables_WhenCalledOnChildBeforeParentPass_ThrowsInvalidOperationException()
        {
            using IObjectResolver root = Container.Build(_ => { });
            using IObjectResolver child = root.CreateChildScope(b => b.Register<InitTracking>(Lifetime.Scoped));

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => child.RunInitializables());

            StringAssert.Contains("parent", exception.Message, "The error names the parent scope's missing pass");
        }

        /// <summary>
        /// After the parent's pass, a child's pass covers the child's own registrations: the parent-owned
        /// participant is initialized once by the parent, the child-owned one once by the child.
        /// </summary>
        [Test]
        public void RunInitializables_WhenCalledOnChildAfterParentPass_InitializesOnlyChildOwnedParticipants()
        {
            using IObjectResolver root = Container.Build(b => b.Register<InitTracking>(Lifetime.Singleton));
            using IObjectResolver child = root.CreateChildScope(b => b.Register<CrossInstanceInit>(Lifetime.Scoped));
            root.RunInitializables();

            child.RunInitializables();

            InitTracking parentOwned = root.Resolve<InitTracking>();
            CrossInstanceInit childOwned = child.Resolve<CrossInstanceInit>();
            Assert.AreEqual(1, parentOwned.InitializeCallCount, "The parent's participant is initialized once, by the parent");
            Assert.IsTrue(childOwned.OtherWasReady, "The child's participant is built and initialized by the child's pass");
        }

        /// <summary>
        /// A pass started from inside an <c>Initialize</c> body is refused with <see cref="InvalidOperationException"/>;
        /// the outer pass completes.
        /// </summary>
        [Test]
        public void RunInitializables_WhenCalledReentrantlyFromInitialize_ThrowsInvalidOperationException()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<ReentrantInitializable>(r => new ReentrantInitializable(r), Lifetime.Singleton);
            });

            resolver.RunInitializables();

            ReentrantInitializable participant = resolver.Resolve<ReentrantInitializable>();
            Assert.IsInstanceOf<InvalidOperationException>(participant.CaughtException, "A re-entrant pass is refused");
        }
    }
}