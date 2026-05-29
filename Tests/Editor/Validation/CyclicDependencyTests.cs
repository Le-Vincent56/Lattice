using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Exceptions;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Validation
{
    /// <summary>
    /// Verifies cycle detection in the dependency graph. Cycles must surface as
    /// <see cref="CyclicDependencyException"/> at <see cref="Container.Build"/> time;
    /// not at first <see cref="IObjectResolver.Resolve{T}"/> call; so misconfiguration
    /// fails fast at startup. Cycle detection runs the same way regardless of lifetime,
    /// since the validator walks constructor parameters, not runtime resolution paths.
    ///
    /// Exerc ises <c>DependencyGraphValidator.Walk</c>'s <c>visited</c> and <c>path</c>
    /// bookkeeping and the <c>CyclicDependencyException</c> path-cutting.
    /// </summary>
    [TestFixture]
    public sealed class CyclicDependencyTests
    {
        // Cycle fixtures live nested inside the test class; they're test-only types
        // not intended for the shared Fixtures folder

        public interface ICycleA
        {
        }

        public interface ICycleB
        {
        }

        /// <summary>
        /// Singleton cycle: <see cref="CycleA"/> constructor takes <see cref="ICycleB"/>.
        /// </summary>
        public sealed class CycleA : ICycleA
        {
            public CycleA(ICycleB b)
            {
            }
        }

        /// <summary>
        /// Singleton cycle: <see cref="CycleB"/> constructor takes <see cref="ICycleA"/>.
        /// </summary>
        public sealed class CycleB : ICycleB
        {
            public CycleB(ICycleA a)
            {
            }
        }

        /// <summary>
        /// Transient cycle: same shape as <see cref="CycleA"/>, used for the Transient test.
        /// </summary>
        public sealed class TransientCycleA : ICycleA
        {
            public TransientCycleA(ICycleB b)
            {
            }
        }

        /// <summary>
        /// Transient cycle: same shape as <see cref="CycleB"/>, used for the Transient test.
        /// </summary>
        public sealed class TransientCycleB : ICycleB
        {
            public TransientCycleB(ICycleA a)
            {
            }
        }


        /// <summary>
        /// Two Singletons forming a 2-step cycle (A -> B -> A) must throw at build time.
        /// The exception's <c>CyclePath</c> must contain at least both types, and the
        /// message must name both implementations for diagnostic clarity.
        /// </summary>
        [Test]
        public void Build_WhenServicesFormCycle_ThrowsCyclicDependencyException()
        {
            CyclicDependencyException ex = Assert.Throws<CyclicDependencyException>(() =>
            {
                Container.Build(b =>
                {
                    b.Register<ICycleA, CycleA>(Lifetime.Singleton);
                    b.Register<ICycleB, CycleB>(Lifetime.Singleton);
                });
            });

            Assert.GreaterOrEqual(ex.CyclePath.Count, 2, "Cycle path must contain at least both types");
            Assert.That(ex.Message, Does.Contain(nameof(CycleA)));
            Assert.That(ex.Message, Does.Contain(nameof(CycleB)));
        }

        /// <summary>
        /// Cycle detection is lifetime-agnostic; Transient cycles fire just as Singleton cycles do.
        /// The validator walks constructor parameter types, not runtime resolution paths,
        /// so the lifetime of the participants doesn't affect the cycle check.
        /// </summary>
        [Test]
        public void Build_WhenTransientServicesFormCycle_ThrowsCyclicDependencyException()
        {
            CyclicDependencyException ex = Assert.Throws<CyclicDependencyException>(() =>
            {
                Container.Build(b =>
                {
                    b.Register<ICycleA, TransientCycleA>(Lifetime.Transient);
                    b.Register<ICycleB, TransientCycleB>(Lifetime.Transient);
                });
            });
        }
    }
}