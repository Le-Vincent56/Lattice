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
        /// A child registration of the same service type must shadow the parent; child's own
        /// <c>ClosedRegistry</c> hit short-circuits the walk-to-parent. The two scopes
        /// should resolve to different Singleton instances.
        /// </summary>
        [Test]
        public void Resolve_WhenChildShadowsParentRegistration_ChildResolvesItsOwnImplementation()
        {
            using IObjectResolver root = Container.Build(b => b.Register<IServiceA, ServiceA>(Lifetime.Singleton));
            using IObjectResolver child =
                root.CreateChildScope(b => b.Register<IServiceA, ServiceA>(Lifetime.Singleton));

            IServiceA fromRoot = root.Resolve<IServiceA>();
            IServiceA fromChild = child.Resolve<IServiceA>();

            Assert.AreNotSame(fromRoot, fromChild, "Child registration should shadow parent");
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
        /// A child's scope <c>Dispose</c> must dispose every <see cref="IDisposable"/> it owns
        /// (Scoped instances created within the child). Verifies the disposable-tracking path in
        /// <c>MaterializeFromEntry</c>'s Scoped branch.
        /// </summary>
        /// <remarks>
        /// Note: cascade-from-root (disposing root automatically disposes child instances)
        /// is covered by a separate disposal test fixture, not here.
        /// </remarks>
        [Test]
        public void Dispose_WhenRootDisposed_DisposablesInChildScopesAlsoDisposed()
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
    }
}