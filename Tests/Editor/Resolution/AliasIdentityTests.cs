using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Resolution
{
    /// <summary>
    /// Verifies alias identity: every <c>As&lt;T&gt;()</c>, <c>AsSelf()</c> and <c>AsImplementedInterfaces()</c> alias
    /// of one Singleton or Scoped registration resolves one implementation instance per cache, while Transient aliases
    /// and separate registrations keep their own instances.
    ///
    /// Exercises <c>RegistrationEntry.Primary</c>, set by <c>Registration.As</c>, and the primary-keyed caches in
    /// <c>Scope.MaterializeFromEntry</c>.
    /// </summary>
    [TestFixture]
    public sealed class AliasIdentityTests
    {
        /// <summary>
        /// Resets the static counters on <see cref="AliasedService"/> and <see cref="AliasedStartable"/>, which every
        /// test in the fixture shares.
        /// </summary>
        [SetUp]
        public void Reset()
        {
            AliasedService.InitializeCount = 0;
            AliasedStartable.StartCount = 0;
        }

        /// <summary>
        /// A Singleton registration aliased with <c>As&lt;T&gt;()</c> is one registration, so resolving its own type
        /// and the alias returns the same instance.
        /// </summary>
        [Test]
        public void Resolve_WhenSingletonResolvedThroughAlias_ReturnsSameInstanceAsPrimary()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<AliasedService>(Lifetime.Singleton).As<IAliasPrimary>();
            });

            AliasedService fromPrimary = resolver.Resolve<AliasedService>();
            IAliasPrimary fromAlias = resolver.Resolve<IAliasPrimary>();

            Assert.AreSame(fromPrimary, fromAlias, "Every alias of one Singleton registration resolves one instance");
        }

        /// <summary>
        /// A Scoped registration aliased with <c>As&lt;T&gt;()</c> resolves one instance per scope through every alias.
        /// </summary>
        [Test]
        public void Resolve_WhenScopedResolvedThroughAlias_ReturnsSameInstanceAsPrimary()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<AliasedService>(Lifetime.Scoped).As<IAliasPrimary>();
            });

            AliasedService fromPrimary = resolver.Resolve<AliasedService>();
            IAliasPrimary fromAlias = resolver.Resolve<IAliasPrimary>();

            Assert.AreSame(fromPrimary, fromAlias, "Every alias of one Scoped registration resolves one instance");
        }

        /// <summary>
        /// <c>AsImplementedInterfaces()</c> adds one alias per interface; the implementation type and every interface
        /// resolve the registration's single instance.
        /// </summary>
        [Test]
        public void Resolve_WhenAsImplementedInterfacesUsed_EveryInterfaceResolvesSameInstance()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<AliasedService>(Lifetime.Singleton).AsImplementedInterfaces();
            });

            AliasedService fromImpl = resolver.Resolve<AliasedService>();
            IAliasPrimary fromPrimary = resolver.Resolve<IAliasPrimary>();
            IAliasSecondary fromSecondary = resolver.Resolve<IAliasSecondary>();

            Assert.AreSame(fromImpl, fromPrimary, "The first interface alias resolves the registration's instance");
            Assert.AreSame(fromImpl, fromSecondary, "The second interface alias resolves the registration's instance");
        }

        /// <summary>
        /// An aliased factory registration is one registration, so the factory runs once however many aliases are
        /// resolved.
        /// </summary>
        [Test]
        public void Resolve_WhenFactoryRegistrationAliased_FactoryInvokedOnce()
        {
            int factoryCalls = 0;
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<AliasedService>(_ => { factoryCalls++; return new AliasedService(); }, Lifetime.Singleton)
                    .As<IAliasPrimary>();
            });

            AliasedService fromPrimary = resolver.Resolve<AliasedService>();
            IAliasPrimary fromAlias = resolver.Resolve<IAliasPrimary>();

            Assert.AreEqual(1, factoryCalls, "The factory builds the registration's instance once");
            Assert.AreSame(fromPrimary, fromAlias, "Both aliases resolve the factory's single instance");
        }

        /// <summary>
        /// An aliased Scoped registration local to a child is owned by that child: both aliases share the child's
        /// instance, and a sibling with its own registration has a different one.
        /// </summary>
        [Test]
        public void Resolve_WhenChildScopedRegistrationAliased_EachSiblingSharesOneInstanceAcrossAliases()
        {
            using IObjectResolver root = Container.Build(_ => { });
            using IObjectResolver siblingA =
                root.CreateChildScope(b => b.Register<AliasedService>(Lifetime.Scoped).As<IAliasPrimary>());
            using IObjectResolver siblingB =
                root.CreateChildScope(b => b.Register<AliasedService>(Lifetime.Scoped).As<IAliasPrimary>());

            AliasedService implFromA = siblingA.Resolve<AliasedService>();
            IAliasPrimary aliasFromA = siblingA.Resolve<IAliasPrimary>();
            AliasedService implFromB = siblingB.Resolve<AliasedService>();

            Assert.AreSame(implFromA, aliasFromA, "Within one child, both aliases resolve the child's instance");
            Assert.AreNotSame(implFromA, implFromB, "Each sibling owns its own instance of a child-local registration");
        }

        /// <summary>
        /// A Transient registration has no cached instance, so its aliases build a new instance on every resolve.
        /// </summary>
        [Test]
        public void Resolve_WhenTransientResolvedThroughAlias_ReturnsNewInstanceEachTime()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<AliasedService>(Lifetime.Transient).As<IAliasPrimary>();
            });

            IAliasPrimary first = resolver.Resolve<IAliasPrimary>();
            IAliasPrimary second = resolver.Resolve<IAliasPrimary>();
            AliasedService fromPrimary = resolver.Resolve<AliasedService>();

            Assert.AreNotSame(first, second, "A Transient alias builds a new instance on every resolve");
            Assert.AreNotSame(first, fromPrimary, "Transient aliases do not share an instance");
        }

        /// <summary>
        /// Identity is per registration call: the same implementation registered twice is two registrations and
        /// two instances.
        /// </summary>
        [Test]
        public void Resolve_WhenSameImplementationRegisteredTwice_ReturnsDifferentInstances()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IAliasPrimary, AliasedService>(Lifetime.Singleton);
                b.Register<IAliasSecondary, AliasedService>(Lifetime.Singleton);
            });

            IAliasPrimary fromFirst = resolver.Resolve<IAliasPrimary>();
            IAliasSecondary fromSecond = resolver.Resolve<IAliasSecondary>();

            Assert.AreNotSame(fromFirst, fromSecond, "Two registration calls are two registrations with two instances");
        }

        /// <summary>
        /// Resolving two aliases of one registration caches one instance, so <c>RunInitializables</c> initializes it
        /// once.
        /// </summary>
        [Test]
        public void RunInitializables_WhenAliasesShareInstance_InitializesSharedInstanceOnce()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<AliasedService>(Lifetime.Singleton).As<IAliasPrimary>();
            });
            _ = resolver.Resolve<AliasedService>();
            _ = resolver.Resolve<IAliasPrimary>();

            resolver.RunInitializables();

            Assert.AreEqual(1, AliasedService.InitializeCount, "One shared instance is initialized once");
        }

        /// <summary>
        /// <c>AsImplementedInterfaces()</c> puts a startable under its own type and under <see cref="IAsyncStartable"/>,
        /// and both buckets are selected. The registration is one instance, so it is started once per call.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenRegistrationAliasedUnderTwoStartableTypes_StartsSharedInstanceOnce()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<AliasedStartable>(Lifetime.Singleton).AsImplementedInterfaces();
            });

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Assert.AreEqual(1, AliasedStartable.StartCount,
                "The registration is selected through two startable buckets but is one instance, started once");
        }
    }
}
