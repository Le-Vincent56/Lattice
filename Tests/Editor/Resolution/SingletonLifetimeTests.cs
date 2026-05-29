using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Resolution
{
    /// <summary>
    /// Verifies Singleton lifetime semantics: one instance per registration,
    /// shared across the entire scope tree (root + all descendants). Also covers
    /// <c>RegisterInstance</c>, which is a Singleton special case where the container
    /// does not call any constructor; the caller-supplied reference is returned as-is
    /// on every Resolve.
    ///
    /// Exercises <c>Scope.MaterializeFromEntry</c>'s Singleton branch, root-scope
    /// caching via <c>Root._singletonCache</c>, and <c>RegistrationEntry.IsPreBuiltInstance</c>
    /// short-circuiting.
    /// </summary>
    [TestFixture]
    public sealed class SingletonLifetimeTests
    {
        /// <summary>
        /// A Singleton registration must produce the same instance on every Resolve call
        /// from the registering scope.
        /// </summary>
        [Test]
        public void Resolve_WhenLifetimeIsSingleton_ReturnsSameInstanceAcrossCalls()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IServiceA, ServiceA>(Lifetime.Singleton);
            });

            IServiceA first = resolver.Resolve<IServiceA>();
            IServiceA second = resolver.Resolve<IServiceA>();

            Assert.AreSame(first, second, "Singleton instances should be the same");
        }

        /// <summary>
        /// Singletons are cross-scope: a child scope resolving the same service type
        /// as the root must receive the same instance the root would. Verifies that
        /// child scopes route Singleton lookups through <c>Scope.Root</c>'s cache rather
        /// than maintaining their own.
        /// </summary>
        [Test]
        public void Resolve_WhenLifetimeIsSingleton_ReturnsSameInstanceAcrossChildScopes()
        {
            using IObjectResolver root = Container.Build(b => { b.Register<IServiceA, ServiceA>(Lifetime.Singleton); });
            using IObjectResolver child = root.CreateChildScope(_ => { });

            IServiceA fromRoot = root.Resolve<IServiceA>();
            IServiceA fromChild = child.Resolve<IServiceA>();

            Assert.AreSame(fromRoot, fromChild, "Singleton instances should be shared across scopes");
        }

        /// <summary>
        /// <c>RegisterInstance</c> must return the exact reference the caller provided;
        /// the container must not wrap it, copy it, or re-instantiate. Verifies the
        /// <c>IsPreBuiltInstance</c> short-circuit in <c>MaterializeFromEntry</c>.
        /// </summary>
        [Test]
        public void RegisterInstance_WhenInstanceProvided_ResolvesSameReference()
        {
            ServiceA pinned = new ServiceA();
            using IObjectResolver resolver = Container.Build(b => { b.RegisterInstance<IServiceA>(pinned); });

            IServiceA resolved = resolver.Resolve<IServiceA>();

            Assert.AreSame(resolved, pinned, "RegisterInstance should return the exact reference provided");
        }
    }
}