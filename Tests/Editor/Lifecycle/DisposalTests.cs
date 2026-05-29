using System;
using System.Collections.Generic;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Verifies the disposal contract:
    /// <list type="bullet">
    ///     <item>Scoped <see cref="IDisposable"/> instances are disposed when the scope is disposed, in reverse-creation order (last created -> first disposed)</item>
    ///     <item>Calling <c>Dispose</c> twice is idempotent; the second call is a no-op</item>
    ///     <item>Transient instances that implement <see cref="IDisposable"/> are tracked on the resolving scope and disposed when that scope is disposed</item>
    /// </list>
    ///
    /// Exercises <c>Scope.Dispose</c>'s reverse-iteration loop,
    /// the <c>_disposed</c> guard, and the IDisposable-tracking branches in
    /// <c>MaterializeFromEntry</c> for Scoped and Transient.
    /// </summary>
    [TestFixture]
    public sealed class DisposalTests
    {
        /// <summary>
        /// Disposable that records the order of <c>Dispose</c> calls in a static list,
        /// keyed by the per-instance <see cref="ID"/>. Used to verify
        /// reverse-creation ordering.
        /// </summary>
        public sealed class OrderedDisposable : IDisposable
        {
            public static readonly List<int> DisposalOrder = new List<int>();
            public int ID { get; }
            public OrderedDisposable(int id) => ID = id;
            public void Dispose() => DisposalOrder.Add(ID);
        }

        /// <summary>
        /// Test-local <see cref="IServiceA"/> implementation that also writes to
        /// <see cref="OrderedDisposable.DisposalOrder"/> on dispose. Lets a single test
        /// verify ordering across two distinct service-type registrations.
        /// </summary>
        private sealed class ServiceAWithDisposable : IServiceA, IDisposable
        {
            private readonly int _id;
            public Guid InstanceID { get; } = Guid.NewGuid();
            public ServiceAWithDisposable(int id) => _id = id;
            public void Dispose() => OrderedDisposable.DisposalOrder.Add(_id);
        }

        [SetUp]
        public void Reset() => OrderedDisposable.DisposalOrder.Clear();

        /// <summary>
        /// Scoped IDisposables registered in a scope must be disposed in reverse-creation order
        /// when the scope is disposed. First-created disposes last; last-created disposes first. Mirrors
        /// the convention that dependents Dispose before their dependencies.
        /// </summary>
        [Test]
        public void Dispose_WhenScopeContainsScopedDisposables_DisposesInReverseCreationOrder()
        {
            IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<OrderedDisposable>(_ => new OrderedDisposable(1), Lifetime.Scoped);

                // Distinct service type so it gets its own RegistrationEntry; both end up
                // in the scope's _disposables list in resolution order
                b.RegisterFactory<IServiceA>(_ => new ServiceAWithDisposable(2), Lifetime.Scoped);
            });

            // Resolve in order: OrderedDisposable(1) first, ServiceAWithDisposable(2) second.
            // Disposable must run in reverse: 2, then 1.
            resolver.Resolve<OrderedDisposable>();
            resolver.Resolve<IServiceA>();
            resolver.Dispose();

            Assert.That(
                OrderedDisposable.DisposalOrder,
                Is.EqualTo(new[] { 2, 1 }),
                "Reverse creation order: last created disposes first"
            );
        }

        /// <summary>
        /// Calling <c>Dispose</c> twice on the same scope must not raise; the second call
        /// is a no-op. Verifies the <c>_disposed</c> latch in <see cref="Scope.Dispose"/>
        /// </summary>
        [Test]
        public void Dispose_WhenCalledTwice_IsIdempotent()
        {
            IObjectResolver resolver = Container.Build(b => b.Register<DisposableService>(Lifetime.Scoped));
            DisposableService disposable = resolver.Resolve<DisposableService>();

            resolver.Dispose();
            resolver.Dispose();

            Assert.IsTrue(disposable.IsDisposed,
                "Disposable still disposed; second Dispose() is a no-op, not an error");
        }

        /// <summary>
        /// Transient instances that implement <see cref="IDisposable"/> must be tracked on
        /// the resolving scope's disposables list, and disposed when that scope is disposed.
        /// Only IDisposable transients are tracked; non-disposable transients are not retained.
        /// </summary>
        [Test]
        public void Dispose_WhenTransientIsDisposable_TrackedAndDisposedWithScope()
        {
            DisposableService captured;
            using (IObjectResolver resolver = Container.Build(b => b.Register<DisposableService>(Lifetime.Transient)))
            {
                captured = resolver.Resolve<DisposableService>();
                Assert.IsFalse(captured.IsDisposed, "Transient should not be disposed yet");
            }

            Assert.IsTrue(captured.IsDisposed, "Transient should be disposed");
        }
    }
}