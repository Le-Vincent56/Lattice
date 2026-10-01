using System;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Hierarchy
{
    /// <summary>
    /// Verifies parent/child scope contracts:
    /// <list type="bullet">
    ///     <item>child scopes inherit parent registrations (resolution walks up the chain on miss)</item>
    ///     <item>child registrations shadow parent registrations of the same service type</item>
    ///     <item>disposing a child does not disposes the parent</item>
    ///     <item>a disposed scope refuses further resolves with <see cref="ObjectDisposedException"/></item>
    ///     <item>a child scope's <c>Dispose</c> disposes its own scoped <see cref="IDisposable"/> instances</item>
    ///     <item>a parent-owned instance and its disposables survive the disposal of a child that requested it</item>
    /// </list>
    ///
    /// Exercises <c>Scope.ResolveInternal</c>'s parent-chain walk, <c>Scope._scopedCache</c>
    /// and <c>_disposables</c> ownership, and <c>Scope.ThrowIfDisposed</c>'s public-surface gate.
    /// </summary>
    [TestFixture]
    public sealed class ScopeHierarchyTests
    {
        /// <summary>
        /// A child scope must resolve a service registered only in the parent.
        /// The miss-on-child and walk-to-parent path in <c>Scope.ResolveInternal</c>.
        /// </summary>
        [Test]
        public void Resolve_WhenServiceRegisteredInParent_ChildScopeFindsIt()
        {
            using IObjectResolver root = Container.Build(b => b.Register<IServiceA, ServiceA>(Lifetime.Singleton));
            using IObjectResolver child = root.CreateChildScope(_ => { });

            IServiceA resolved = child.Resolve<IServiceA>();

            Assert.IsNotNull(resolved, "Child scope should find parent registration");
        }

        /// <summary>
        /// A child registration of the same service type shadows the parent's for resolves that start in the
        /// child; the child's own registry hit short-circuits the walk to the parent, so the two scopes resolve
        /// different instances.
        /// </summary>
        [Test]
        public void Resolve_WhenChildShadowsParentRegistration_ChildResolvesItsOwnImplementation()
        {
            using IObjectResolver root = Container.Build(b => b.Register<IServiceA, ServiceA>(Lifetime.Singleton));
            using IObjectResolver child =
                root.CreateChildScope(b => b.Register<IServiceA, AlternateServiceA>(Lifetime.Scoped));

            IServiceA fromRoot = root.Resolve<IServiceA>();
            IServiceA fromChild = child.Resolve<IServiceA>();

            Assert.AreNotSame(fromRoot, fromChild, "Child registration should shadow parent");
            Assert.IsInstanceOf<AlternateServiceA>(fromChild, "The child resolves its own registration");
        }

        /// <summary>
        /// Disposing a child scope must not affect the parent. The parent must continue to resolve
        /// the same Singleton instance after the child is disposed.
        /// </summary>
        [Test]
        public void Dispose_WhenChildScopeDisposed_ParentScopeStillResolves()
        {
            using IObjectResolver root = Container.Build(b => b.Register<IServiceA, ServiceA>(Lifetime.Singleton));
            IObjectResolver child = root.CreateChildScope(_ => { });

            _ = child.Resolve<IServiceA>();
            child.Dispose();
            IServiceA fromRoot = root.Resolve<IServiceA>();

            Assert.IsNotNull(fromRoot, "Root scope should still resolve");
        }

        /// <summary>
        /// A disposed scope must refuse further resolves. Verifies <c>ThrowIfDisposed</c>
        /// gates the public <see cref="IObjectResolver.Resolve{T}"/> entry point.
        /// </summary>
        [Test]
        public void Dispose_WhenChildScopeDisposed_ChildScopeRefusesFurtherResolves()
        {
            using IObjectResolver root = Container.Build(b => b.Register<IServiceA, ServiceA>(Lifetime.Singleton));
            IObjectResolver child = root.CreateChildScope(_ => { });
            child.Dispose();

            Assert.Throws<ObjectDisposedException>(() => child.Resolve<IServiceA>(),
                "Child scope should refuse further resolves");
        }

        /// <summary>
        /// A child scope's <c>Dispose</c> disposes every <see cref="IDisposable"/> it owns, which includes the
        /// Scoped instances created from its own registrations. Root-to-child cascade is a separate contract.
        /// </summary>
        [Test]
        public void Dispose_WhenChildScopeDisposed_ScopedDisposableOwnedByChildIsDisposed()
        {
            using IObjectResolver root = Container.Build(_ => { });
            DisposableService captured;
            using (IObjectResolver child = root.CreateChildScope(b => b.Register<DisposableService>(Lifetime.Scoped)))
            {
                captured = child.Resolve<DisposableService>();
                Assert.IsFalse(captured.IsDisposed, "Child scope's disposable should not be disposed yet");
            }

            Assert.IsTrue(captured.IsDisposed, "Child scope's disposable should be disposed");
        }

        /// <summary>
        /// The Transient disposables created while building a parent-owned Scoped service belong to the parent,
        /// so disposing the child that first requested the service leaves them alive, and disposing the parent
        /// disposes them.
        /// </summary>
        [Test]
        public void Dispose_WhenChildScopeDisposed_ParentOwnedScopedKeepsItsTransientDependencyUndisposed()
        {
            IObjectResolver root = Container.Build(b =>
            {
                b.Register<DisposableService>(Lifetime.Transient);
                b.Register<DisposableDependent>(Lifetime.Scoped);
            });
            IObjectResolver child = root.CreateChildScope(_ => { });
            DisposableDependent fromChild = child.Resolve<DisposableDependent>();

            child.Dispose();
            bool disposedWithChild = fromChild.Dependency.IsDisposed;
            root.Dispose();
            bool disposedWithRoot = fromChild.Dependency.IsDisposed;

            Assert.IsFalse(disposedWithChild, "The parent's instance must stay usable after a child is disposed");
            Assert.IsTrue(disposedWithRoot, "The dependency belongs to the parent and is disposed with it");
        }

        /// <summary>
        /// A parent-owned Scoped instance is cached by the parent, so it is still the same instance after the
        /// child that first requested it has been disposed.
        /// </summary>
        [Test]
        public void Resolve_WhenParentOwnedScopedRequestedAfterChildDisposed_ReturnsSameInstance()
        {
            using IObjectResolver root = Container.Build(b => b.Register<IServiceA, ServiceA>(Lifetime.Scoped));
            IObjectResolver child = root.CreateChildScope(_ => { });
            IServiceA fromChild = child.Resolve<IServiceA>();

            child.Dispose();
            IServiceA fromRoot = root.Resolve<IServiceA>();

            Assert.AreSame(fromChild, fromRoot, "The parent keeps the instance it owns");
        }

        /// <summary>
        /// The Transient disposables created while building a root Singleton belong to the root, so disposing
        /// the child that first requested the Singleton leaves them alive.
        /// </summary>
        [Test]
        public void Dispose_WhenChildScopeDisposed_RootSingletonKeepsItsTransientDependencyUndisposed()
        {
            using IObjectResolver root = Container.Build(b =>
            {
                b.Register<DisposableService>(Lifetime.Transient);
                b.Register<DisposableDependent>(Lifetime.Singleton);
            });
            IObjectResolver child = root.CreateChildScope(_ => { });
            DisposableDependent fromChild = child.Resolve<DisposableDependent>();

            child.Dispose();

            Assert.IsFalse(fromChild.Dependency.IsDisposed, "The root's Singleton must stay usable after a child is disposed");
        }
    }
}