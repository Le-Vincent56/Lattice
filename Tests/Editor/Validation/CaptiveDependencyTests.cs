using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Exceptions;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Validation
{
    /// <summary>
    /// Verifies the captive dependency rules at <see cref="Container.Build"/> time.
    /// <list type="bullet">
    ///     <item><b>Singleton -> Scoped</b> throws; the Singleton would capture the Scoped instance past its inteded scope lifetime</item>
    ///     <item><b>Scoped -> Transient</b> is permitted; Transients are short-lived and safe to nest</item>
    ///     <item><b>Singleton -> Singleton</b> is permitted; both share the same root-scope cache</item>
    /// </list>
    ///
    /// Exercises <c>DependencyGraphValidator.CheckCaptive</c>.
    /// </summary>
    [TestFixture]
    public sealed class CaptiveDependencyTests
    {
        /// <summary>
        /// A Singleton whose constructor takes a Scoped dependency must fail at build time.
        /// The exception must report the outer/inner types and lifetimes accurately so the
        /// developer can find the offending pair without a stack trace.
        /// </summary>
        [Test]
        public void Build_WhenSingletonDependsOnScoped_ThrowsCaptiveDependencyException()
        {
            CaptiveDependencyException ex = Assert.Throws<CaptiveDependencyException>(() =>
            {
                Container.Build(b =>
                {
                    // ServiceB's constructor takes IServiceA. Registering ServiceB as Singleton
                    // with ServiceA as Scoped triggers the captive check on the IServiceA parameter.
                    b.Register<IServiceA, ServiceA>(Lifetime.Scoped);
                    b.Register<IServiceB, ServiceB>(Lifetime.Singleton);
                });
            });

            Assert.That(ex.OuterLifetime, Is.EqualTo(Lifetime.Singleton));
            Assert.That(ex.InnerLifetime, Is.EqualTo(Lifetime.Scoped));
            Assert.That(ex.OuterType, Is.EqualTo(typeof(ServiceB)));
            Assert.That(ex.InnerType, Is.EqualTo(typeof(ServiceA)));
        }

        /// <summary>
        /// Scoped depending on Transient is allowed; Transient instances are short-lived and
        /// can be safely held by a Scoped instance for the scope's lifetime. Build must succeed and the resolved
        /// instance must be retrievable.
        /// </summary>
        [Test]
        public void Build_WhenScopedDependsOnTransient_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                using IObjectResolver resolver = Container.Build(b =>
                {
                    b.Register<IServiceA, ServiceA>(Lifetime.Transient);
                    b.Register<IServiceB, ServiceB>(Lifetime.Scoped);
                });

                // Resolve to ensure the runtime path is also clean
                _ = resolver.Resolve<IServiceB>();
            });
        }

        /// <summary>
        /// Singleton depending on Singleton is the canonical safe case; both live
        /// for the container's full lifetime, no captivity risk.
        /// </summary>
        [Test]
        public void Build_WhenSingletonDependsOnSingleton_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                using IObjectResolver resolver = Container.Build(b =>
                {
                    b.Register<IServiceA, ServiceA>(Lifetime.Singleton);
                    b.Register<IServiceB, ServiceB>(Lifetime.Singleton);
                });

                _ = resolver.Resolve<IServiceB>();
            });
        }
    }
}