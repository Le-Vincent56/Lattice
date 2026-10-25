using System;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Internal;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Verifies that disposal is a lifecycle state: a disposed scope reports <c>Disposed</c> whatever state it was in
    /// before, a faulted one included.
    ///
    /// Exercises the latch of <c>Scope.Dispose</c> and <c>Scope.State</c>.
    /// </summary>
    [TestFixture]
    public sealed class DisposalStateTests
    {
        /// <summary>
        /// Disposing an initialized scope moves it to the disposed state.
        /// </summary>
        [Test]
        public void Dispose_WhenCalled_ScopeStateIsDisposed()
        {
            IObjectResolver resolver = Container.Build(b => b.Register<InitTracking>(Lifetime.Singleton));
            resolver.RunInitializables();

            resolver.Dispose();

            Assert.AreEqual(LifecycleState.Disposed, ((Scope)resolver).State, "A disposed scope reports the disposed state");
        }

        /// <summary>
        /// Disposal is the one way out of the faulted state.
        /// </summary>
        [Test]
        public void Dispose_WhenScopeFaulted_ScopeStateIsDisposed()
        {
            IObjectResolver resolver = Container.Build(b => b.Register<ThrowingInitializable>(Lifetime.Singleton));
            Assert.Catch<Exception>(() => resolver.RunInitializables(), "Precondition: the pass faults the scope");

            resolver.Dispose();

            Assert.AreEqual(LifecycleState.Disposed, ((Scope)resolver).State, "Disposing a faulted scope moves it to the disposed state");
        }
    }
}
