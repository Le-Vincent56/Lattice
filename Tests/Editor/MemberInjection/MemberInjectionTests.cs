using NUnit.Framework;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Internal;
using Didionysymus.Lattice.Tests.Editor.Fixtures;

namespace Didionysymus.Lattice.Tests.Editor.MemberInjection
{
    /// <summary>
    /// Pins the Step 4 member-injection contract on POCOs:
    /// <list type="bullet">
    ///     <item><c>[Inject]</c> on a public field is populated by <c>IObjectResolver.Inject</c></item>
    ///     <item><c>[Inject]</c> on a property reaches non-public setters (e.g. <c>{ get; private set; }</c>)</item>
    ///     <item><c>[Inject]</c> on a method invokes the method once with each parameter resolved from this scope</item>
    ///     <item><c>[Inject(Optional = true)]</c> tolerates a missing registration: the member receives default and no exception escapes</item>
    ///     <item>Phase 1 / Phase 2 ordering: by the time <see cref="IInitializable.Initialize"/> runs, every <c>[Inject]</c> member is bound</item>
    ///     <item>The <see cref="InjectionPlanCache"/> yields exactly one <see cref="InjectionPlan"/> per concrete type, regardless of how many instances are injected</item>
    /// </list>
    ///
    /// Exercises <c>Scope.Inject</c>, <c>InjectInternal</c>, <c>ResolveMember</c>, <c>ResolveMethodParam</c>, and the
    /// expression-tree-compiled setters/invoker built by <see cref="InjectionPlanCache"/>. The cache-hit test reaches
    /// into the internal cache via <c>InternalsVisibleTo</c> granted by <c>DI/AssemblyInfo.cs</c>.
    /// </summary>
    /// <remarks>
    /// v1 contract reminder: constructor injection runs during <c>Resolve</c>; member injection (fields/properties/methods)
    /// runs only when <c>Inject(instance)</c> is called explicitly. There is no auto-member-injection on Resolve. The
    /// Unity-side <c>LifetimeScopeBehaviour</c> in Step 4 is what calls <c>Inject</c> on every scene MonoBehaviour for you.
    /// </remarks>
    [TestFixture]
    public sealed class MemberInjectionTests
    {
        /// <summary>
        /// <c>[Inject]</c> on a public field must be populated end-to-end through the
        /// expression-tree-compiled field setter built once per type by <see cref="InjectionPlanCache.BuildPlan"/>.
        /// Verifies the field-setter path (declared field, public, no special accessors).
        /// </summary>
        [Test]
        public void Inject_WhenInstanceHasInjectField_PopulatesField()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IServiceA, ServiceA>(Lifetime.Singleton));
            FieldInjectTarget target = new FieldInjectTarget();

            resolver.Inject(target);

            Assert.IsNotNull(target.ServiceA,
                "Field setter compiled by InjectionPlanCache populated the [Inject] field");
        }

        /// <summary>
        /// <c>[Inject]</c> on a property whose setter is private must still populate; the
        /// expression-tree property setter uses <c>PropertyInfo.GetSetMethod(nonPublic: true)</c>
        /// so private setters are reachable. Verifies the property-setter path against an
        /// auto-property with <c>{ get; private set; }</c> shape.
        /// </summary>
        [Test]
        public void Inject_WhenInstanceHasInjectProperty_PopulatesProperty()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IServiceA, ServiceA>(Lifetime.Singleton));
            PropertyInjectTarget target = new PropertyInjectTarget();

            resolver.Inject(target);

            Assert.IsNotNull(target.ServiceA, "Compiled property setter reached the private set; accessor");
        }

        /// <summary>
        /// <c>[Inject]</c> on a method must invoke that method once with every parameter resolved from this scope.
        /// Verifies the at-most-one method discovery path in <see cref="InjectionPlanCache"/> and the per-parameter
        /// resolution loop in <c>Scope.InjectInternal</c>.
        /// </summary>
        [Test]
        public void Inject_WhenInstanceHasInjectMethod_InvokesMethodWithResolvedArguments()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IServiceA, ServiceA>(Lifetime.Singleton);
                b.Register<IServiceB, ServiceB>(Lifetime.Singleton);
            });
            MethodInjectTarget target = new MethodInjectTarget();

            // Pre-condition: properties start null because the [Inject] method assigns them in its body
            Assert.IsNull(target.ServiceA, "Pre-condition: method has not yet been invoked");

            resolver.Inject(target);

            Assert.IsNotNull(target.ServiceA, "Method invoker assigned ServiceA via the method body");
            Assert.IsNotNull(target.ServiceB, "Method invoker assigned ServiceB via the method body");
        }

        /// <summary>
        /// <c>[Inject(Optional = true)]</c> must tolerate a missing registration: the member is left at its default
        /// value (null for reference types) and no exception escapes <c>Inject</c>. Verifies the
        /// <c>catch (RegistrationNotFoundException) when (member.Optional)</c> path in <c>Scope.ResolveMember</c>.
        /// </summary>
        [Test]
        public void Inject_WhenOptionalAndNotRegistered_AssignsNullWithoutThrowing()
        {
            // Build with an empty configuration so no IServiceA registration exists; the optional [Inject]
            // must not propagate the resulting RegistrationNotFoundException out of Inject.
            using IObjectResolver resolver = Container.Build(_ => { });
            OptionalInjectTarget target = new OptionalInjectTarget();

            Assert.DoesNotThrow(() => resolver.Inject(target),
                "Optional [Inject] swallows RegistrationNotFoundException");
            Assert.IsNull(target.ServiceA, "Reference-type optional member left at default (null)");
        }

        /// <summary>
        /// Pins the strict two-phase contract: Phase 1 binds all <c>[Inject]</c> members, then Phase 2 runs
        /// <see cref="IInitializable.Initialize"/>. By the time <c>Initialize</c> runs, every <c>[Inject]</c> member
        /// of the same instance, and of any peer instance in the same materialization wave, is observably bound.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The flow:
        /// <list type="number">
        ///     <item>
        ///         <c>Resolve&lt;TwoPhaseTarget&gt;</c> constructs the instance via the (parameterless) constructor and parks it in the singleton cache;
        ///         the <c>[Inject]</c> property <c>Trainer</c> is null at this point because v1 Resolve does not auto-inject members.
        ///     </item>
        ///     <item><c>Inject(target)</c> runs Phase 1: walks the cached <see cref="InjectionPlan"/>, sets <c>Trainer</c> via the compiled property setter.</item>
        ///     <item><c>RunInitializables</c> runs Phase 2 across the singleton cache; <c>target.Initialize</c> records <c>Trainer.Name</c>.</item>
        /// </list>
        /// The recorded name being non-null is sufficient evidence that Phase 2 ran AFTER Phase 1; null would mean
        /// the contract is broken and <c>Initialize</c> ran before <c>Inject</c>.
        /// </para>
        /// </remarks>
        [Test]
        public void Inject_WhenInjectAndInitializableCombined_InitializeSeesInjectedFields()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<ITrainer, CinderTrainer>(Lifetime.Singleton);

                // TwoPhaseTarget must be registered so RunInitializables can find it via the singleton cache walk
                b.Register<TwoPhaseTarget>(Lifetime.Singleton);
            });

            TwoPhaseTarget target = resolver.Resolve<TwoPhaseTarget>();
            resolver.Inject(target);
            resolver.RunInitializables();

            Assert.AreEqual("Cinder", target.TrainerNameAtInitialize,
                "Initialize ran in Phase 2 with Trainer already populated by Phase 1; null would indicate the phases ran out of order");
        }

        /// <summary>
        /// The per-type <see cref="InjectionPlan"/> must be built exactly once and cached;
        /// subsequent <c>Inject</c> calls on instances of the same concrete type reuse it.
        /// Verifies the <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.GetOrAdd"/>
        /// hit path in <see cref="InjectionPlanCache"/>.
        /// </summary>
        /// <remarks>
        /// The cache is process-static (no per-test reset). Earlier tests in the run may have already primed it
        /// for <see cref="FieldInjectTarget"/>, so we assert a delta: that the SECOND injection adds zero new plans,
        /// rather than asserting an absolute count. This keeps the test robust to NUnit's undefined test ordering.
        /// </remarks>
        [Test]
        public void Inject_WhenSameTypeInjectedTwice_ReusesCachedInjectionPlan()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IServiceA, ServiceA>(Lifetime.Singleton));

            // Warm the cache (or hit a pre-warmed entry from an earlier test in this run)
            resolver.Inject(new FieldInjectTarget());
            int afterFirst = InjectionPlanCache.CachedPlanCount;

            // Second injection of the same type: the cache must serve the existing plan; count is unchanged
            resolver.Inject(new FieldInjectTarget());
            int afterSecond = InjectionPlanCache.CachedPlanCount;

            Assert.AreEqual(afterFirst, afterSecond,
                "Second Inject of the same concrete type does not add a new plan to the cache");
        }
    }
}