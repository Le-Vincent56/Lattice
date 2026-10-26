using NUnit.Framework;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;

namespace Didionysymus.Lattice.Tests.Editor.MemberInjection
{
    /// <summary>
    /// Pins how inherited <c>[Inject]</c> members are bound:
    /// <list type="bullet">
    ///     <item>a private <c>[Inject]</c> field or property declared on a base class is populated on a derived instance</item>
    ///     <item>an <c>[Inject]</c> method declared only on a base class is invoked</item>
    ///     <item>when the derived class and a base class both declare an <c>[Inject]</c> method, only the derived one runs</item>
    ///     <item>a virtual property marked <c>[Inject]</c> on both the base and the override is resolved twice</item>
    /// </list>
    ///
    /// Exercises <c>Scope.Inject</c> and the plan built by <c>InjectionPlanCache</c>, which walks every level of the
    /// type hierarchy with <c>DeclaredOnly</c> and takes the most-derived level that declares an <c>[Inject]</c> method.
    /// </summary>
    [TestFixture]
    public sealed class InheritedInjectionTests
    {
        /// <summary>
        /// A private <c>[Inject]</c> field declared on an abstract base must be populated when a derived instance is
        /// injected.
        /// </summary>
        [Test]
        public void Inject_WhenBaseClassDeclaresPrivateInjectField_PopulatesIt()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IServiceA, ServiceA>(Lifetime.Singleton);
                b.Register<IServiceB, ServiceB>(Lifetime.Singleton);
            });
            DerivedFromInjectingBase target = new DerivedFromInjectingBase();

            resolver.Inject(target);

            Assert.AreSame(resolver.Resolve<IServiceA>(), target.FieldA, "The base class's private field was bound");
        }

        /// <summary>
        /// A private <c>[Inject]</c> property declared on an abstract base must be populated when a derived instance
        /// is injected.
        /// </summary>
        [Test]
        public void Inject_WhenBaseClassDeclaresPrivateInjectProperty_PopulatesIt()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IServiceA, ServiceA>(Lifetime.Singleton);
                b.Register<IServiceB, ServiceB>(Lifetime.Singleton);
            });
            DerivedFromInjectingBase target = new DerivedFromInjectingBase();

            resolver.Inject(target);

            Assert.AreSame(resolver.Resolve<IServiceB>(), target.InjectedB, "The base class's private property was bound");
        }

        /// <summary>
        /// When only a base class declares an <c>[Inject]</c> method, injecting a derived instance must invoke it.
        /// </summary>
        [Test]
        public void Inject_WhenOnlyBaseClassDeclaresInjectMethod_InvokesIt()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IServiceA, ServiceA>(Lifetime.Singleton);
                b.Register<IServiceB, ServiceB>(Lifetime.Singleton);
            });
            DerivedWithoutInjectMethod target = new DerivedWithoutInjectMethod();

            resolver.Inject(target);

            Assert.AreSame(resolver.Resolve<IServiceA>(), target.BaseArgument, "The base class's [Inject] method ran");
        }

        /// <summary>
        /// When the derived class and its base both declare an <c>[Inject]</c> method, only the derived method must
        /// run; the base method is shadowed.
        /// </summary>
        [Test]
        public void Inject_WhenDerivedAndBaseDeclareInjectMethods_InvokesOnlyDerived()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IServiceA, ServiceA>(Lifetime.Singleton);
                b.Register<IServiceB, ServiceB>(Lifetime.Singleton);
            });
            DerivedWithInjectMethod target = new DerivedWithInjectMethod();

            resolver.Inject(target);

            Assert.AreSame(resolver.Resolve<IServiceB>(), target.DerivedArgument, "The derived [Inject] method ran");
            Assert.IsNull(target.BaseArgument, "The base class's [Inject] method was shadowed");
        }

        /// <summary>
        /// A virtual property marked <c>[Inject]</c> on both the base and the override appears once per level in the
        /// plan, so it is resolved and set twice. Pins the documented edge: mark one level only.
        /// </summary>
        [Test]
        public void Inject_WhenOverriddenPropertyMarkedOnBothLevels_ResolvesItTwice()
        {
            int resolves = 0;
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IServiceA>(_ => { resolves++; return new ServiceA(); }, Lifetime.Transient);
            });
            VirtualInjectOverride target = new VirtualInjectOverride();

            resolver.Inject(target);

            Assert.AreEqual(2, resolves, "The property was resolved once per level that marks it");
            Assert.IsNotNull(target.Service, "The property was set");
        }
    }
}
