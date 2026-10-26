using NUnit.Framework;
using Didionysymus.Lattice.Runtime.Exceptions;
using Didionysymus.Lattice.Runtime.Internal;
using Didionysymus.Lattice.Tests.Editor.Fixtures;

namespace Didionysymus.Lattice.Tests.Editor.MemberInjection
{
    /// <summary>
    /// Pins the eligibility metadata the Unity adapter reads to decide which components need injection:
    /// <list type="bullet">
    ///     <item>a type with no <c>[Inject]</c> member has no injection members</item>
    ///     <item>an <c>[Inject]</c> member inherited from a base class counts</item>
    ///     <item>an <c>[Inject]</c> method alone counts</item>
    ///     <item>a type whose plan cannot be built counts as injectable, and the failure is not cached away</item>
    /// </list>
    ///
    /// Exercises <c>InjectionPlanCache.HasInjectionMembers</c> and <c>InjectionPlanCache.GetPlan</c>, reached through
    /// the core's <c>InternalsVisibleTo</c> grant to this assembly.
    /// </summary>
    [TestFixture]
    public sealed class InjectionMetadataTests
    {
        /// <summary>
        /// A type whose plan has no member and no method must report no injection members.
        /// </summary>
        [Test]
        public void HasInjectionMembers_WhenTypeDeclaresNoInjectMembers_ReturnsFalse()
        {
            bool result = InjectionPlanCache.HasInjectionMembers(typeof(NoInjectMembers));

            Assert.IsFalse(result, "An unmarked field is not an injection member");
        }

        /// <summary>
        /// A type whose only injection members are declared on a base class must report injection members.
        /// </summary>
        [Test]
        public void HasInjectionMembers_WhenOnlyBaseClassDeclaresInjectMember_ReturnsTrue()
        {
            bool result = InjectionPlanCache.HasInjectionMembers(typeof(DerivedFromInjectingBase));

            Assert.IsTrue(result, "Inherited [Inject] members count");
        }

        /// <summary>
        /// A type whose only injection member is an <c>[Inject]</c> method must report injection members.
        /// </summary>
        [Test]
        public void HasInjectionMembers_WhenOnlyInjectMethodDeclared_ReturnsTrue()
        {
            bool result = InjectionPlanCache.HasInjectionMembers(typeof(InjectMethodOnly));

            Assert.IsTrue(result, "An [Inject] method alone counts");
        }

        /// <summary>
        /// A type whose plan build throws must count as injectable, so the bind reports the failure; the failure is
        /// not cached away, so <c>GetPlan</c> still throws afterwards.
        /// </summary>
        [Test]
        public void HasInjectionMembers_WhenPlanBuildThrows_ReturnsTrue()
        {
            bool result = InjectionPlanCache.HasInjectionMembers(typeof(TwoInjectMethods));

            Assert.IsTrue(result, "A type whose plan cannot be built counts as injectable");
            Assert.Throws<MultipleInjectMethodsException>(() => InjectionPlanCache.GetPlan(typeof(TwoInjectMethods)));
        }
    }
}
