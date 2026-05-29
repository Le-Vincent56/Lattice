using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.OpenGenerics
{
    /// <summary>
    /// Verifies open-generic resolution mechanics: closed-on-demand promotion returns
    /// the right concrete type, the closed registration is cached after the first resolve (so Scoped/Singleton
    /// lifetime semantics carry through), and distinct closed instantiations resolve to distinct cache entries.
    ///
    /// Exercises <c>ContainerBuilder.RegisterOpenGeneric</c>, <c>Scope.PromoteOpenGeneric</c>, and the closed-registry
    /// caching path in <c>Scope.ResolveInternal</c>.
    /// </summary>
    [TestFixture]
    public sealed class OpenGenericResolutionTests
    {
        /// <summary>
        /// First resolve of a closed type whose open generic is registered must promote
        /// to a closed <c>RegistrationEntry</c> and return the registered implementation.
        /// </summary>
        [Test]
        public void Resolve_WhenOpenGenericRegistered_ClosedResolutionReturnsImpl()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterOpenGeneric(typeof(IRepository<>), typeof(Repository<>), Lifetime.Scoped);
            });

            IRepository<MonsterDefinition> repo = resolver.Resolve<IRepository<MonsterDefinition>>();

            Assert.IsNotNull(repo, "Resolve of closed type backed by an open registration must succeed.");
            Assert.IsInstanceOf<Repository<MonsterDefinition>>(repo,
                "Resolved instance must be the registered implementation closed against MonsterDefinition.");
        }

        /// <summary>
        /// After closed-on-demand promotion, the resulting <c>RegistrationEntry</c> is cached
        /// in <see cref="Didionysymus.Lattice.Internal.Registry.ClosedRegistry"/>. Scoped lifetime then
        /// guarantees subsequent resolves on the same scope return the same instance.
        /// </summary>
        [Test]
        public void Resolve_WhenSameClosedGenericResolvedTwice_ReturnsSameInstanceForScoped()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterOpenGeneric(typeof(IRepository<>), typeof(Repository<>), Lifetime.Scoped);
            });

            IRepository<MonsterDefinition> first = resolver.Resolve<IRepository<MonsterDefinition>>();
            IRepository<MonsterDefinition> second = resolver.Resolve<IRepository<MonsterDefinition>>();

            Assert.AreSame(first, second,
                "Closed registration is cached after first resolve; Scoped lifetime returns same instance on subsequent resolves.");
        }

        /// <summary>
        /// Each distinct closed type promotes its own <c>RegistrationEntry</c>. Resolving two
        /// different closures produces two different concrete instances, even though both share the same
        /// open-generic registration.
        /// </summary>
        [Test]
        public void Resolve_WhenDifferentClosedGenericsRequested_ReturnsDifferentImpls()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterOpenGeneric(typeof(IRepository<>), typeof(Repository<>), Lifetime.Scoped);
            });

            IRepository<MonsterDefinition> monsters = resolver.Resolve<IRepository<MonsterDefinition>>();
            IRepository<AbilityDefinition> abilities = resolver.Resolve<IRepository<AbilityDefinition>>();

            Assert.IsInstanceOf<Repository<MonsterDefinition>>(monsters);
            Assert.IsInstanceOf<Repository<AbilityDefinition>>(abilities);
            Assert.AreNotSame(monsters, abilities,
                "Different closed types must resolve to independent implementations.");
        }
    }
}