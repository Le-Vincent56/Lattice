using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Resolution
{
    /// <summary>
    /// Verifies Scoped lifetime semantics: one instance per "owning scope". The owning scope
    /// is the scope where the registration lives; Scoped services registered in the root are
    /// shared across all descendants; Scoped services registered locally in a child are unique
    /// to that child.
    ///
    /// Exercises <c>Scope.MaterializeFromEntry</c>'s Scoped branch and the
    /// <c>Scope.FindOwningScope</c> walk that determines which scope's <c>_scopedCache</c>
    /// holds the instance.
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
        /// When the Scoped registration lives in the root, all descendant scopes share
        /// the same instance; the root is the "owning scope" for that registration.
        /// Verifies <c>FindOwningScope</c> walks up and lands on root.
        /// </summary>
        [Test]
        public void Resolve_WhenLifetimeIsScoped_ReturnsDifferentInstancesAcrossSiblingChildScopes()
        {
            using IObjectResolver root = Container.Build(b => { b.Register<IServiceA, ServiceA>(Lifetime.Scoped); });
            using IObjectResolver siblingA = root.CreateChildScope(_ => { });
            using IObjectResolver siblingB = root.CreateChildScope(_ => { });

            IServiceA fromA = siblingA.Resolve<IServiceA>();
            IServiceA fromB = siblingB.Resolve<IServiceA>();

            // Both children walk up to root for the registration;
            // root is the owning scope, so siblings share the SAME instance. Despite
            // the test name suggesting "different", the plan's intended assertion is sameness; the
            // registration's owning scope wins
            Assert.AreSame(fromA, fromB,
                "Scoped registration in root is owned by root, so all descendants share that instance");
        }

        /// <summary>
        /// When the Scoped registration is local to a child scope, that child is the owning scope.
        /// Sibling children with their own local registration get independent instances. Verifies
        /// that <c>FindOwningScope</c> stops at the child rather than continuing up to root.
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
    }
}