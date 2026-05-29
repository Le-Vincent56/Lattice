using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Decorators
{
    /// <summary>
    /// Verifies the decorator contract:
    /// <list type="bullet">
    ///     <item>a single decorator wraps the inner service exactly one</item>
    ///     <item>multiple decorators stack innermost-first by registration order (first registered = innermost; last registered = outermost)</item>
    ///     <item>decorators participate in lifetime caching; a SCoped decorator-wrapped service returns the same wrapped reference on repeated resolves within the scope</item>
    /// </list>
    ///
    /// Exercises <c>Scope.WrapDecorators</c> and <c>Scope.BuildDecorator</c> in <c>Scope.cs</c>
    /// </summary>
    [TestFixture]
    public sealed class DecoratorTests
    {
        /// <summary>
        /// One decorator wraps the inner. The resolved instance must be the decorator type,
        /// and emit order shows the inner appends first, then the decorator.
        /// </summary>
        [Test]
        public void Resolve_WithSingleDecorator_WrapsInnerOnce()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<ILoggable, RealLoggable>(Lifetime.Scoped);
                b.RegisterDecorator<ILoggable, FirstDecorator>(Lifetime.Scoped);
            });

            ILoggable loggable = resolver.Resolve<ILoggable>();
            loggable.Emit("hello");

            Assert.That(loggable.Log, Is.EqualTo(new[] { "real:hello", "first:hello" }));
            Assert.IsInstanceOf<FirstDecorator>(loggable);
        }

        [Test]
        public void Resolve_WithMultipleDecorators_AppliesInnermostFirstByRegistrationOrder()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<ILoggable, RealLoggable>(Lifetime.Scoped);
                b.RegisterDecorator<ILoggable, FirstDecorator>(Lifetime.Scoped);
                b.RegisterDecorator<ILoggable, SecondDecorator>(Lifetime.Scoped);
            });

            ILoggable loggable = resolver.Resolve<ILoggable>();
            loggable.Emit("X");

            Assert.That(loggable.Log, Is.EqualTo(new[] { "real:X", "first:X", "second:X" }));
            Assert.IsInstanceOf<SecondDecorator>(loggable, "Last-registered decorator is the outer most");
        }

        /// <summary>
        /// A Scoped registration with a Scoped decorator must cache the wrapped instance;
        /// repeated Resolve calls return the same outer-decorator reference. Verifies the decorator-wrapping happens inside the lifetime-caching
        /// path, not outside it.
        /// </summary>
        [Test]
        public void Resolve_WithScopedDecorator_DecoratorParticipatesInLifetimeCache()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<ILoggable, RealLoggable>(Lifetime.Scoped);
                b.RegisterDecorator<ILoggable, FirstDecorator>(Lifetime.Scoped);
            });

            ILoggable first = resolver.Resolve<ILoggable>();
            ILoggable second = resolver.Resolve<ILoggable>();

            Assert.AreSame(first, second, "Repeated Resolve calls should return the same inner-decorator reference");
        }
    }
}