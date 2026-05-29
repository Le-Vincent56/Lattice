using System;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.OpenGenerics
{
    /// <summary>
    /// Verifies the <c>PreserveClosedGenerics</c> pre-build path: declaring a closed type at build time
    /// pre-populates the closed registry so the first runtime resolve of that type pays no expression-compile cost.
    /// Also pins the <see cref="GenericTypes.Close"/> helper that lets installers compute closed-type arrays
    /// from an open definition and closing-arg types.
    ///
    /// Exercises <c>Container.Build</c>'s pre-build wire-up, <c>Registry.PrebuildPReservedClosedGenerics</c>,
    /// and <c>GenericTypes.Close</c>.
    /// </summary>
    [TestFixture]
    public sealed class PreserveClosedGenericsTests
    {
        /// <summary>
        /// Build-time pre-build must not throw when preserved closed types correspond
        /// to a registered open generic. Verifies that <c>PrebuildPreservedClosedGenerics</c>
        /// runs cleanly during <c>Container.Build</c>.
        /// </summary>
        [Test]
        public void PreserveClosedGenerics_WhenCalledWithClosedTypes_DoesNotThrowAtBuild()
        {
            Assert.DoesNotThrow(() =>
            {
                using IObjectResolver resolver = Container.Build(b =>
                {
                    b.RegisterOpenGeneric(typeof(IRepository<>), typeof(Repository<>), Lifetime.Scoped);
                    b.PreserveClosedGenerics(
                        typeof(IRepository<MonsterDefinition>),
                        typeof(IRepository<AbilityDefinition>)
                    );
                });
            });
        }

        /// <summary>
        /// A closed type declared via <c>PreserveClosedGenerics</c> msut resolve correctly even though no prior
        /// open-generic resolve has triggered closed-on-demand promotion. Pre-build is the only mechanism populating
        /// <c>ClosedRegistry</c> for this type.
        /// </summary>
        [Test]
        public void Resolve_WhenClosedTypeWasPreserved_ResolvesEvenWithoutPriorOpenGenericResolve()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterOpenGeneric(typeof(IRepository<>), typeof(Repository<>), Lifetime.Scoped);
                b.PreserveClosedGenerics(typeof(IRepository<MonsterDefinition>));
            });

            IRepository<MonsterDefinition> repo = resolver.Resolve<IRepository<MonsterDefinition>>();

            Assert.IsInstanceOf<Repository<MonsterDefinition>>(repo,
                "Pre-built closed type must resolve to its concrete impl on first resolve.");
        }

        /// <summary>
        /// <see cref="GenericTypes.Close"/> on a single-arity open type must produce one closed
        /// type per supplied type argument, in the same order. Verifies the helper that installers use to feed <c>PreserveClosedGenerics</c>.
        /// </summary>
        [Test]
        public void GenericTypes_Close_ProducesArrayOfClosedTypesForSingleArity()
        {
            Type[] closed = GenericTypes.Close(
                typeof(IRepository<>),
                typeof(MonsterDefinition),
                typeof(AbilityDefinition)
            );

            Assert.AreEqual(2, closed.Length, "Close should produce one closed type per supplied type argument.");
            Assert.AreEqual(typeof(IRepository<MonsterDefinition>), closed[0]);
            Assert.AreEqual(typeof(IRepository<AbilityDefinition>), closed[1]);
        }
    }
}