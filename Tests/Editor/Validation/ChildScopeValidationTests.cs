using System;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Exceptions;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Validation
{
    /// <summary>
    /// Verifies what <see cref="IObjectResolver.CreateChildScope"/> checks before a child scope exists: the child's
    /// registrations are walked against the child's registry, then the creating scope's, then each further
    /// ancestor's, so a cycle that crosses into the immediate parent is reported at creation, a dependency shadowed
    /// in the creating scope is validated against the shadowing registration, and legal cross-scope edges pass.
    ///
    /// Exercises the registry chain <c>Scope.CreateChildScope</c> hands to <c>DependencyGraphValidator.Validate</c>.
    /// </summary>
    [TestFixture]
    public sealed class ChildScopeValidationTests
    {
        /// <summary>
        /// An <see cref="IServiceA"/> that depends on <see cref="IServiceB"/>. Paired with <see cref="ServiceB"/>,
        /// which depends on <see cref="IServiceA"/>, it forms a two-node cycle across a scope boundary.
        /// </summary>
        public sealed class ServiceANeedingB : IServiceA
        {
            public Guid InstanceID { get; } = Guid.NewGuid();
            public IServiceB B { get; }
            public ServiceANeedingB(IServiceB b) => B = b;
        }

        /// <summary>
        /// A child registration whose dependency lives in the immediate parent and depends back on the child's
        /// service must be rejected at creation. The parent built without error because its own dependency was
        /// unregistered at the time; the child closes the cycle.
        /// </summary>
        [Test]
        public void CreateChildScope_WhenChildRegistrationCyclesThroughImmediateParent_ThrowsCyclicDependencyException()
        {
            using IObjectResolver root = Container.Build(b => b.Register<IServiceB, ServiceB>(Lifetime.Transient));

            CyclicDependencyException ex = Assert.Throws<CyclicDependencyException>(() =>
            {
                root.CreateChildScope(b => b.Register<IServiceA, ServiceANeedingB>(Lifetime.Transient));
            });

            CollectionAssert.Contains(ex.CyclePath, typeof(ServiceANeedingB));
            CollectionAssert.Contains(ex.CyclePath, typeof(ServiceB));
        }

        /// <summary>
        /// When the creating scope shadows a grandparent registration, the child is validated against the
        /// shadowing registration, the same one <c>Resolve</c> would use. Here the grandparent's leaf
        /// <see cref="ServiceA"/> is shadowed by a parent <see cref="ServiceANeedingB"/>, and a grandchild that
        /// registers <see cref="ServiceB"/> closes a cycle that exists only through the parent's entry.
        /// </summary>
        [Test]
        public void CreateChildScope_WhenDependencyIsShadowedInImmediateParent_ValidatesAgainstShadowingRegistration()
        {
            using IObjectResolver grandparent = Container.Build(b => b.Register<IServiceA, ServiceA>(Lifetime.Transient));
            using IObjectResolver parent =
                grandparent.CreateChildScope(b => b.Register<IServiceA, ServiceANeedingB>(Lifetime.Transient));

            Assert.Throws<CyclicDependencyException>(() =>
            {
                parent.CreateChildScope(b => b.Register<IServiceB, ServiceB>(Lifetime.Transient));
            }, "The parent's shadowing registration, not the grandparent's leaf, must be what validation sees");
        }

        /// <summary>
        /// A child Scoped service that depends on a parent Singleton is a legal edge and must still create.
        /// </summary>
        [Test]
        public void CreateChildScope_WhenChildScopedDependsOnParentSingleton_DoesNotThrow()
        {
            using IObjectResolver root = Container.Build(b => b.Register<IServiceA, ServiceA>(Lifetime.Singleton));

            Assert.DoesNotThrow(() =>
            {
                using IObjectResolver child = root.CreateChildScope(b => b.Register<IServiceB, ServiceB>(Lifetime.Scoped));
                _ = child.Resolve<IServiceB>();
            });
        }

        /// <summary>
        /// A Singleton is cached and disposed by the root, so one registered in a child would outlive the child and
        /// be rebuilt on every creation of that child. Creation rejects it and names the service type.
        /// </summary>
        [Test]
        public void CreateChildScope_WhenChildRegistersSingleton_ThrowsChildSingletonRegistrationException()
        {
            using IObjectResolver root = Container.Build(_ => { });

            ChildSingletonRegistrationException ex = Assert.Throws<ChildSingletonRegistrationException>(() =>
            {
                root.CreateChildScope(b => b.Register<IServiceA, ServiceA>(Lifetime.Singleton));
            });

            Assert.AreEqual(typeof(IServiceA), ex.ServiceType);
            Assert.AreEqual(typeof(ServiceA), ex.ImplType);
        }

        /// <summary>
        /// A Singleton factory in a child is rejected for the same reason as a Singleton type registration.
        /// </summary>
        [Test]
        public void CreateChildScope_WhenChildRegistersSingletonFactory_ThrowsChildSingletonRegistrationException()
        {
            using IObjectResolver root = Container.Build(_ => { });

            Assert.Throws<ChildSingletonRegistrationException>(() =>
            {
                root.CreateChildScope(b => b.RegisterFactory<IServiceA>(_ => new ServiceA(), Lifetime.Singleton));
            });
        }

        /// <summary>
        /// An open-generic Singleton registration in a child would promote to child-owned Singleton entries on
        /// demand, so it is rejected at creation like a closed one.
        /// </summary>
        [Test]
        public void CreateChildScope_WhenChildRegistersOpenGenericSingleton_ThrowsChildSingletonRegistrationException()
        {
            using IObjectResolver root = Container.Build(_ => { });

            ChildSingletonRegistrationException ex = Assert.Throws<ChildSingletonRegistrationException>(() =>
            {
                root.CreateChildScope(b =>
                {
                    b.RegisterOpenGeneric(typeof(IRepository<>), typeof(Repository<>), Lifetime.Singleton);
                });
            });

            Assert.AreEqual(typeof(IRepository<>), ex.ServiceType);
        }

        /// <summary>
        /// Preserving a closed generic in a child against a parent's open Singleton registration would create a
        /// child-owned Singleton entry, so it is rejected; resolving the closed type through the parent already
        /// yields the shared instance.
        /// </summary>
        [Test]
        public void CreateChildScope_WhenChildPreservesClosedGenericOfParentOpenSingleton_ThrowsChildSingletonRegistrationException()
        {
            using IObjectResolver root = Container.Build(b =>
            {
                b.RegisterOpenGeneric(typeof(IRepository<>), typeof(Repository<>), Lifetime.Singleton);
            });

            ChildSingletonRegistrationException ex = Assert.Throws<ChildSingletonRegistrationException>(() =>
            {
                root.CreateChildScope(b => b.PreserveClosedGenerics(typeof(IRepository<MonsterDefinition>)));
            });

            Assert.AreEqual(typeof(IRepository<MonsterDefinition>), ex.ServiceType);
        }

        /// <summary>
        /// <c>RegisterInstance</c> supplies an object the container neither builds nor disposes, so a child may
        /// register one and resolves the exact reference.
        /// </summary>
        [Test]
        public void CreateChildScope_WhenChildRegistersInstance_DoesNotThrow()
        {
            using IObjectResolver root = Container.Build(_ => { });
            ServiceA supplied = new ServiceA();

            IObjectResolver child = null;
            Assert.DoesNotThrow(() => { child = root.CreateChildScope(b => b.RegisterInstance<IServiceA>(supplied)); });
            IServiceA resolved = child.Resolve<IServiceA>();
            child.Dispose();

            Assert.AreSame(supplied, resolved, "A child may hold an externally supplied instance");
        }
    }
}
