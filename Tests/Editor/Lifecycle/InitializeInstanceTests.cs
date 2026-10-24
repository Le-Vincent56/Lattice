using System;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Exceptions;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Verifies <c>InitializeInstance</c>: it initializes an externally supplied object once per scope, by reference,
    /// through the same tracking the initialization pass uses; it ignores objects that are not
    /// <see cref="IInitializable"/>; it requires the scope to have completed its pass; and a thrown
    /// <c>Initialize</c> faults the scope.
    ///
    /// Exercises <c>Scope.InitializeInstance</c> and the shared <c>_initialized</c> set.
    /// </summary>
    [TestFixture]
    public sealed class InitializeInstanceTests
    {
        /// <summary>
        /// Resets the static counter on <see cref="ValueEqualInitializable"/>.
        /// </summary>
        [SetUp]
        public void Reset()
        {
            ValueEqualInitializable.InitializeCount = 0;
        }

        /// <summary>
        /// The first call initializes the object and returns true; the second call for the same object does nothing
        /// and returns false.
        /// </summary>
        [Test]
        public void InitializeInstance_WhenCalledTwiceForSameInstance_InitializesOnceAndReturnsFalseSecondTime()
        {
            using IObjectResolver resolver = Container.Build(_ => { });
            resolver.RunInitializables();
            InitTracking supplied = new InitTracking();

            bool first = resolver.InitializeInstance(supplied);
            bool second = resolver.InitializeInstance(supplied);

            Assert.IsTrue(first, "The first call runs Initialize");
            Assert.IsFalse(second, "The second call is skipped");
            Assert.AreEqual(1, supplied.InitializeCallCount, "The object is initialized once");
        }

        /// <summary>
        /// An object that is not <see cref="IInitializable"/> is ignored and the call returns false.
        /// </summary>
        [Test]
        public void InitializeInstance_WhenInstanceIsNotInitializable_ReturnsFalse()
        {
            using IObjectResolver resolver = Container.Build(_ => { });
            resolver.RunInitializables();

            bool ran = resolver.InitializeInstance(new object());

            Assert.IsFalse(ran, "A non-initializable object is ignored");
        }

        /// <summary>
        /// A <c>RegisterInstance</c> value is caller-owned; the explicit call is how its owner initializes it.
        /// </summary>
        [Test]
        public void InitializeInstance_WhenInstanceIsRegisteredInstance_InitializesIt()
        {
            InitTracking supplied = new InitTracking();
            using IObjectResolver resolver = Container.Build(b => b.RegisterInstance(supplied));
            resolver.RunInitializables();

            bool ran = resolver.InitializeInstance(supplied);

            Assert.IsTrue(ran, "The explicit call initializes a supplied instance");
            Assert.AreEqual(1, supplied.InitializeCallCount, "The supplied instance is initialized once");
        }

        /// <summary>
        /// The pass and the explicit call share one tracking set: a cached service the pass initialized is skipped.
        /// </summary>
        [Test]
        public void InitializeInstance_WhenInstanceWasInitializedByPass_ReturnsFalse()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<InitTracking>(Lifetime.Singleton));
            resolver.RunInitializables();
            InitTracking cached = resolver.Resolve<InitTracking>();

            bool ran = resolver.InitializeInstance(cached);

            Assert.IsFalse(ran, "An instance the pass initialized is not initialized again");
            Assert.AreEqual(1, cached.InitializeCallCount, "One initialization in total");
        }

        /// <summary>
        /// The explicit call requires the scope's own pass to have completed, so a supplied object is never
        /// initialized before the services it depends on.
        /// </summary>
        [Test]
        public void InitializeInstance_WhenScopeNotInitialized_ThrowsInvalidOperationException()
        {
            using IObjectResolver resolver = Container.Build(_ => { });

            InvalidOperationException exception =
                Assert.Throws<InvalidOperationException>(() => resolver.InitializeInstance(new InitTracking()));

            StringAssert.Contains("RunInitializables", exception.Message, "The error says which call must come first");
        }

        /// <summary>
        /// A thrown <c>Initialize</c> surfaces as <see cref="LifecycleException"/> and faults the scope, which then
        /// refuses a pass.
        /// </summary>
        [Test]
        public void InitializeInstance_WhenInitializeThrows_ThrowsLifecycleExceptionAndFaultsScope()
        {
            using IObjectResolver resolver = Container.Build(_ => { });
            resolver.RunInitializables();

            LifecycleException exception =
                Assert.Throws<LifecycleException>(() => resolver.InitializeInstance(new ThrowingInitializable()));

            Assert.AreEqual(typeof(ThrowingInitializable), exception.ParticipantType, "The failure names the participant");
            Assert.Throws<InvalidOperationException>(() => resolver.RunInitializables(), "The scope is faulted");
        }

        /// <summary>
        /// A null instance is an argument error.
        /// </summary>
        [Test]
        public void InitializeInstance_WhenInstanceIsNull_ThrowsArgumentNullException()
        {
            using IObjectResolver resolver = Container.Build(_ => { });
            resolver.RunInitializables();

            Assert.Throws<ArgumentNullException>(() => resolver.InitializeInstance(null), "A null instance is rejected");
        }

        /// <summary>
        /// A disposed scope refuses the call like every other entry point.
        /// </summary>
        [Test]
        public void InitializeInstance_WhenScopeDisposed_ThrowsObjectDisposedException()
        {
            IObjectResolver resolver = Container.Build(_ => { });
            resolver.RunInitializables();
            resolver.Dispose();

            Assert.Throws<ObjectDisposedException>(() => resolver.InitializeInstance(new InitTracking()), "A disposed scope refuses the call");
        }

        /// <summary>
        /// Supplied objects are tracked by reference: two distinct objects that compare equal are both initialized.
        /// </summary>
        [Test]
        public void InitializeInstance_WhenInstancesAreEqualByValue_InitializesBoth()
        {
            using IObjectResolver resolver = Container.Build(_ => { });
            resolver.RunInitializables();

            bool first = resolver.InitializeInstance(new ValueEqualInitializable());
            bool second = resolver.InitializeInstance(new ValueEqualInitializable());

            Assert.IsTrue(first && second, "Both calls run Initialize");
            Assert.AreEqual(2, ValueEqualInitializable.InitializeCount, "Distinct instances are both initialized");
        }
    }
}
