using Didionysymus.Lattice.Runtime;

namespace Didionysymus.Lattice.Tests.Editor.Fixtures
{
    /// <summary>
    /// Abstract base that declares a private <c>[Inject]</c> field and a private <c>[Inject]</c> property, with
    /// public read-only accessors, so a test can see that injecting a derived instance binds members declared on a base.
    /// </summary>
    public abstract class InjectingBase
    {
        [Inject] private IServiceA _fieldA;
        [Inject] private IServiceB PropertyB { get; set; }
        public IServiceA FieldA => _fieldA;
        public IServiceB InjectedB => PropertyB;
    }

    /// <summary>
    /// Derives from <see cref="InjectingBase"/> and declares no members, so every injection member it has is
    /// inherited.
    /// </summary>
    public sealed class DerivedFromInjectingBase : InjectingBase
    {
    }

    /// <summary>
    /// Abstract base that declares a private <c>[Inject]</c> method recording its argument in
    /// <see cref="BaseArgument"/>.
    /// </summary>
    public abstract class InjectMethodBase
    {
        public IServiceA BaseArgument { get; private set; }

        /// <summary>
        /// Records the injected service in <see cref="BaseArgument"/>.
        /// </summary>
        /// <param name="a">The service resolved from the injecting scope.</param>
        [Inject]
        private void ConstructBase(IServiceA a)
        {
            BaseArgument = a;
        }
    }

    /// <summary>
    /// Derives from <see cref="InjectMethodBase"/> without an <c>[Inject]</c> method of its own, so the base
    /// level's method is the one invoked.
    /// </summary>
    public sealed class DerivedWithoutInjectMethod : InjectMethodBase
    {
    }

    /// <summary>
    /// Derives from <see cref="InjectMethodBase"/> and declares its own <c>[Inject]</c> method, which shadows the
    /// base level's method.
    /// </summary>
    public sealed class DerivedWithInjectMethod : InjectMethodBase
    {
        public IServiceB DerivedArgument { get; private set; }

        /// <summary>
        /// Records the injected service in <see cref="DerivedArgument"/>.
        /// </summary>
        /// <param name="b">The service resolved from the injecting scope.</param>
        [Inject]
        public void Construct(IServiceB b)
        {
            DerivedArgument = b;
        }
    }

    /// <summary>
    /// Base whose virtual property is marked <c>[Inject]</c>; <see cref="VirtualInjectOverride"/> marks the
    /// override too.
    /// </summary>
    public class VirtualInjectBase
    {
        [Inject] public virtual IServiceA Service { get; set; }
    }

    /// <summary>
    /// Overrides <see cref="VirtualInjectBase.Service"/> and marks the override <c>[Inject]</c> as well, so the
    /// plan holds the property once per level.
    /// </summary>
    public sealed class VirtualInjectOverride : VirtualInjectBase
    {
        [Inject] public override IServiceA Service { get => base.Service; set => base.Service = value; }
    }

    /// <summary>
    /// Type with a public field of a service type but no <c>[Inject]</c> attribute anywhere, so its plan has no
    /// member and no method.
    /// </summary>
    public sealed class NoInjectMembers
    {
        public IServiceA Unmarked;
    }

    /// <summary>
    /// Type whose only injection member is an <c>[Inject]</c> method.
    /// </summary>
    public sealed class InjectMethodOnly
    {
        /// <summary>
        /// Receives the injected service and does nothing with it.
        /// </summary>
        /// <param name="a">The service resolved from the injecting scope.</param>
        [Inject]
        public void Construct(IServiceA a)
        {
        }
    }

    /// <summary>
    /// Type that declares two <c>[Inject]</c> methods on one level, so building its plan throws
    /// <c>MultipleInjectMethodsException</c>.
    /// </summary>
    public sealed class TwoInjectMethods
    {
        /// <summary>
        /// First of the two <c>[Inject]</c> methods; does nothing.
        /// </summary>
        /// <param name="a">The service resolved from the injecting scope.</param>
        [Inject]
        public void First(IServiceA a)
        {
        }

        /// <summary>
        /// Second of the two <c>[Inject]</c> methods; does nothing.
        /// </summary>
        /// <param name="b">The service resolved from the injecting scope.</param>
        [Inject]
        public void Second(IServiceB b)
        {
        }
    }
}
