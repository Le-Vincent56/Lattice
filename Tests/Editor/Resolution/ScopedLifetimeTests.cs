using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Exceptions;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Resolution
{
    /// <summary>
    /// Verifies Scoped lifetime semantics: one instance per owning scope, where the owning scope is the scope
    /// whose registry holds the registration. A Scoped registration in the root is shared by every descendant and
    /// built with the root's resolution context; a Scoped registration local to a child is unique to that child.
    ///
    /// Exercises the Scoped branch of <c>Scope.MaterializeFromEntry</c>, which caches on, activates with, and
    /// tracks disposables in the scope the entry belongs to.
    /// </summary>
    [TestFixture]
    public sealed class ScopedLifetimeTests
    {
        /// <summary>
        /// Within a single scope, repeated Resolve calls for a Scoped registration
        /// must reutrn the same instance. The trivial within-scope case.
        /// </summary>
        [Test]
        public void Resolve_WhenLifetimeIsScoped_ReturnsSameInstanceWithinScope()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IServiceA, ServiceA>(Lifetime.Scoped);
            });

            IServiceA first = resolver.Resolve<IServiceA>();
            IServiceA second = resolver.Resolve<IServiceA>();

            Assert.AreSame(first, second, "Scoped instances should be the same");
        }

        /// <summary>
        /// When the Scoped registration lives in the parent, every descendant shares the parent's instance:
        /// the parent is the owning scope, so two sibling children resolve the same object.
        /// </summary>
        [Test]
        public void Resolve_WhenScopedRegisteredInParent_ReturnsSameInstanceAcrossSiblingChildScopes()
        {
            using IObjectResolver root = Container.Build(b => { b.Register<IServiceA, ServiceA>(Lifetime.Scoped); });
            using IObjectResolver siblingA = root.CreateChildScope(_ => { });
            using IObjectResolver siblingB = root.CreateChildScope(_ => { });

            IServiceA fromA = siblingA.Resolve<IServiceA>();
            IServiceA fromB = siblingB.Resolve<IServiceA>();

            Assert.AreSame(fromA, fromB,
                "A Scoped registration in the parent is owned by the parent, so siblings share its instance");
        }

        /// <summary>
        /// When the Scoped registration is local to a child scope, that child is the owning scope.
        /// Sibling children with their own local registration get independent instances, each cached by the
        /// child whose registry holds the entry rather than by the root.
        /// </summary>
        [Test]
        public void Resolve_WhenScopedRegistrationIsLocalToChild_DifferentSiblingsHaveDifferentInstances()
        {
            using IObjectResolver root = Container.Build(_ => { });
            using IObjectResolver siblingA =
                root.CreateChildScope(b => b.Register<IServiceA, ServiceA>(Lifetime.Scoped));
            using IObjectResolver siblingB =
                root.CreateChildScope(b => b.Register<IServiceA, ServiceA>(Lifetime.Scoped));

            IServiceA fromA = siblingA.Resolve<IServiceA>();
            IServiceA fromB = siblingB.Resolve<IServiceA>();

            Assert.AreNotSame(fromA, fromB,
                "Different siblings due to different owning scopes, meaning they should be different instances.");
            Assert.AreSame(siblingA.Resolve<IServiceA>(), fromA,
                "Sibling A should have the same instance as sibling B, despite being different scopes");
            Assert.AreSame(siblingB.Resolve<IServiceA>(), fromB,
                "Sibling B should have the same instance as sibling A, despite being different scopes");
        }

        /// <summary>
        /// A Scoped service registered in the parent is built with the parent's resolution context, so a child
        /// registration of one of its dependencies does not reach it, even when the child is the first requester.
        /// </summary>
        [Test]
        public void Resolve_WhenChildOverridesDependencyOfParentOwnedScoped_ParentInstanceUsesParentDependency()
        {
            using IObjectResolver root = Container.Build(b =>
            {
                b.Register<IServiceA, ServiceA>(Lifetime.Transient);
                b.Register<IServiceB, ServiceB>(Lifetime.Scoped);
            });
            using IObjectResolver child = root.CreateChildScope(b =>
            {
                b.Register<IServiceA, AlternateServiceA>(Lifetime.Transient);
            });

            IServiceB fromChild = child.Resolve<IServiceB>();
            IServiceB fromRoot = root.Resolve<IServiceB>();

            Assert.IsInstanceOf<ServiceA>(fromChild.A,
                "A parent-owned Scoped service is built from the parent's registrations, not the child's");
            Assert.AreSame(fromChild, fromRoot, "The parent-owned instance is the one the parent resolves too");
        }

        /// <summary>
        /// A Scoped factory registered in the parent receives the parent as its resolver, so a child registration
        /// of a type the factory resolves does not reach it.
        /// </summary>
        [Test]
        public void Resolve_WhenParentOwnedScopedFactoryResolvedFromChild_FactoryResolvesThroughOwningScope()
        {
            using IObjectResolver root = Container.Build(b =>
            {
                b.Register<IServiceA, ServiceA>(Lifetime.Transient);
                b.RegisterFactory<IServiceB>(r => new ServiceB(r.Resolve<IServiceA>()), Lifetime.Scoped);
            });
            using IObjectResolver child = root.CreateChildScope(b =>
            {
                b.Register<IServiceA, AlternateServiceA>(Lifetime.Transient);
            });

            IServiceB fromChild = child.Resolve<IServiceB>();

            Assert.IsInstanceOf<ServiceA>(fromChild.A,
                "A parent-owned Scoped factory resolves through the parent, not through the requesting child");
        }

        /// <summary>
        /// A parent-owned Scoped service is built from the parent's view of the registry, so a dependency that
        /// only a child registers does not satisfy it: the resolve fails instead of capturing the child's registration.
        /// </summary>
        [Test]
        public void Resolve_WhenParentOwnedScopedDependencyRegisteredOnlyInChild_ThrowsRegistrationNotFoundException()
        {
            using IObjectResolver root = Container.Build(b => { b.Register<IServiceB, ServiceB>(Lifetime.Scoped); });
            using IObjectResolver child = root.CreateChildScope(b =>
            {
                b.Register<IServiceA, ServiceA>(Lifetime.Transient);
            });

            RegistrationNotFoundException ex =
                Assert.Throws<RegistrationNotFoundException>(() => child.Resolve<IServiceB>());

            Assert.AreEqual(typeof(IServiceA), ex.RequestedType,
                "The parent cannot see the child's registration, so the missing dependency is the one it needs");
        }
    }
}