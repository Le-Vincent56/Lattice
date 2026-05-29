using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Resolution
{
    /// <summary>
    /// Verifies Transient lifetime semantics: every <c>Resolve</c> call must return
    /// a fresh instance, including for transitive constructor dependencies.
    ///
    /// First real resolution test in the suite, exercising the wiring from end-to-end:
    /// <c>Container.Build</c> -> <c>ContainerBuilder.Register</c> -> <c>ReflectionActivatorFactory</c>
    /// -> <c>Scope.MaterializeFromEntry</c>'s Transient branch
    /// </summary>
    [TestFixture]
    public sealed class TransientLifetimeTests
    {
        /// <summary>
        /// A Transient registration must produce a distinct object on every Resolve call.
        /// Asserts both reference inequality and identity inequality
        /// via <c>Guid InstanceID</c>; the latter catches any future fixture refactor
        /// that might accidentally share state across instances (e.g., static caching).
        /// </summary>
        [Test]
        public void Resolve_WhenLifetimeIsTransient_ReturnsNewInstanceEachCall()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IServiceA, ServiceA>(Lifetime.Transient);
            });

            IServiceA first = resolver.Resolve<IServiceA>();
            IServiceA second = resolver.Resolve<IServiceA>();

            Assert.AreNotSame(first, second, "Transient instances should be distinct");
            Assert.AreNotEqual(first.InstanceID, second.InstanceID, "Transient instances should have distinct IDs");
        }

        /// <summary>
        /// Transient resolution must recursively inject constructor dependencies, and each
        /// Transient dependency must itself be a fresh instance per outer resolve. Verifies
        /// the activator's parameter-resolution path through <c>ReflectionActivatorFactory.Build</c>.
        /// </summary>
        [Test]
        public void Resolve_WhenLifetimeIsTransient_ResolvesConstructorDependencies()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IServiceA, ServiceA>(Lifetime.Transient);
                b.Register<IServiceB, ServiceB>(Lifetime.Transient);
            });

            IServiceB b1 = resolver.Resolve<IServiceB>();
            IServiceB b2 = resolver.Resolve<IServiceB>();

            Assert.AreNotSame(b1, b2, "Transient dependencies should be distinct");
            Assert.IsNotNull(b1.A, "Transient dependencies should be injected");
            Assert.AreNotSame(b1.A, b2.A);
        }
    }
}