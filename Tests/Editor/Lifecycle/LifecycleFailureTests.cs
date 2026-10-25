using System;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Exceptions;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Verifies what a failed initialization pass leaves behind: the failure names the participant and carries its
    /// exception, the scope is faulted and refuses later passes (its own and its children's), later participants are
    /// never initialized, a construction failure during the pass faults the scope too, and a faulted scope still
    /// resolves and disposes.
    ///
    /// Exercises <c>Scope.RunInitializables</c>, its lifecycle state and <c>LifecycleException</c>.
    /// </summary>
    [TestFixture]
    public sealed class LifecycleFailureTests
    {
        /// <summary>
        /// A thrown <c>Initialize</c> surfaces as a <see cref="DependencyResolutionException"/> that names the
        /// participant and carries the original exception; the scope then refuses another pass, and the participant
        /// after the failing one is never initialized.
        /// </summary>
        [Test]
        public void RunInitializables_WhenInitializeThrows_LaterParticipantIsNotInitializedAndScopeRefusesLaterPasses()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<ThrowingInitializable>(Lifetime.Singleton);
                b.Register<InitTracking>(Lifetime.Singleton);
            });

            DependencyResolutionException failure =
                Assert.Catch<DependencyResolutionException>(() => resolver.RunInitializables());

            Assert.IsInstanceOf<InvalidOperationException>(failure.InnerException, "The participant's own exception is the inner exception");
            StringAssert.Contains(nameof(ThrowingInitializable), failure.Message, "The failure names the participant");
            Assert.Throws<InvalidOperationException>(() => resolver.RunInitializables(), "A faulted scope refuses a later pass");
            Assert.AreEqual(0, resolver.Resolve<InitTracking>().InitializeCallCount, "Participants after the failure are not initialized");
        }

        /// <summary>
        /// A faulted parent blocks its children: the child's pass throws <see cref="InvalidOperationException"/>.
        /// </summary>
        [Test]
        public void RunInitializables_WhenParentFaulted_ChildPassThrowsInvalidOperationException()
        {
            using IObjectResolver root = Container.Build(b => b.Register<ThrowingInitializable>(Lifetime.Singleton));
            using IObjectResolver child = root.CreateChildScope(b => b.Register<InitTracking>(Lifetime.Scoped));
            _ = root.Resolve<ThrowingInitializable>();
            Assert.Catch<Exception>(() => root.RunInitializables());

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => child.RunInitializables());

            StringAssert.Contains("parent", exception.Message, "The error names the parent scope");
        }

        /// <summary>
        /// A participant whose dependency is unregistered builds fine but fails at the pass with the resolution
        /// exception, and that failed pass faults the scope.
        /// </summary>
        [Test]
        public void RunInitializables_WhenParticipantConstructionFails_ThrowsResolutionExceptionAndFaultsScope()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<NeedsUnregisteredDependency>(Lifetime.Singleton));

            Assert.Throws<RegistrationNotFoundException>(() => resolver.RunInitializables(), "Building a participant with a missing dependency fails at the pass");

            Assert.Throws<InvalidOperationException>(() => resolver.RunInitializables(), "The failed pass faults the scope");
        }

        /// <summary>
        /// A faulted scope still resolves, so its owner can inspect and tear it down.
        /// </summary>
        [Test]
        public void Resolve_WhenScopeFaulted_StillResolves()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<ThrowingInitializable>(Lifetime.Singleton);
                b.Register<InitTracking>(Lifetime.Singleton);
            });
            _ = resolver.Resolve<ThrowingInitializable>();
            Assert.Catch<Exception>(() => resolver.RunInitializables());

            InitTracking resolved = resolver.Resolve<InitTracking>();

            Assert.IsNotNull(resolved, "A faulted scope still resolves");
        }

        /// <summary>
        /// A faulted scope still disposes what it owns.
        /// </summary>
        [Test]
        public void Dispose_WhenScopeFaulted_DisposesTrackedInstances()
        {
            IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<ThrowingInitializable>(Lifetime.Singleton);
                b.Register<DisposableService>(Lifetime.Scoped);
            });
            DisposableService disposable = resolver.Resolve<DisposableService>();
            _ = resolver.Resolve<ThrowingInitializable>();
            Assert.Catch<Exception>(() => resolver.RunInitializables());

            resolver.Dispose();

            Assert.IsTrue(disposable.IsDisposed, "A faulted scope still disposes what it owns");
        }

        /// <summary>
        /// A thrown <c>Initialize</c> surfaces as <see cref="LifecycleException"/> naming the participant type and the
        /// phase, with the participant's exception inside.
        /// </summary>
        [Test]
        public void RunInitializables_WhenInitializeThrows_ThrowsLifecycleExceptionNamingParticipantAndPhase()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<ThrowingInitializable>(Lifetime.Singleton));

            LifecycleException exception = Assert.Throws<LifecycleException>(() => resolver.RunInitializables());

            Assert.AreEqual(typeof(ThrowingInitializable), exception.ParticipantType, "The failure names the participant type");
            Assert.AreEqual(LifecyclePhase.Initialize, exception.Phase, "The failure names the phase");
            Assert.IsInstanceOf<InvalidOperationException>(exception.InnerException, "The participant's exception is inside");
        }

        /// <summary>
        /// A child of a faulted parent is refused with the parent's fault as inner exception, quoted in the message.
        /// </summary>
        [Test]
        public void RunInitializables_WhenParentFaulted_ThrowsQuotingParentFault()
        {
            IObjectResolver root = Container.Build(_ => { });
            root.RunInitializables();
            IObjectResolver child = root.CreateChildScope(_ => { });
            LifecycleException rootFault = Assert.Throws<LifecycleException>(() => root.InitializeInstance(new ThrowingInitializable()));

            InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => child.RunInitializables());

            Assert.AreSame(rootFault, refusal.InnerException, "The refusal carries the parent's fault");
            StringAssert.Contains(rootFault.Message, refusal.Message, "The refusal quotes the parent's fault");
        }
    }
}
