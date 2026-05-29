using Didionysymus.Lattice.Runtime;

namespace Didionysymus.Lattice.Tests.Editor.Fixtures
{
    /// <summary>
    /// POCO target with a single <c>[Inject]</c>-annotated public field. Used to verify
    /// <c>IObjectResolver.Inject</c> assigns through field setters end-to-end.
    /// </summary>
    public sealed class FieldInjectTarget
    {
        [Inject] public IServiceA ServiceA;
    }

    /// <summary>
    /// POCO target with a single <c>[Inject]</c>-annotated property whose setter is private.
    /// Verifies the expression-tree property setter reaches non-public accessors via
    /// <c>PropertyInfo.GetSetMethod(nonPublic: true)</c>.
    /// </summary>
    public sealed class PropertyInjectTarget
    {
        [Inject] public IServiceA ServiceA { get; private set; }
    }

    /// <summary>
    /// POCO target with an <c>[Inject]</c>-annotated method. Verifies the method invoker
    /// is compiled and called once with each parameter resolved from the active scope.
    /// </summary>
    public sealed class MethodInjectTarget
    {
        public IServiceA ServiceA { get; private set; }
        public IServiceB ServiceB { get; private set; }

        [Inject]
        public void Construct(IServiceA a, IServiceB b)
        {
            ServiceA = a;
            ServiceB = b;
        }
    }

    /// <summary>
    /// POCO target with an <c>[Inject(Optional = true)]</c> field.
    /// When the type is not registered, <c>[Inject</c> must compelte without throwing and
    /// leave the field at its default (null) value.
    /// </summary>
    public sealed class OptionalInjectTarget
    {
        [Inject(Optional = true)] public IServiceA ServiceA;
    }

    /// <summary>
    /// Trainer interface used by the two-phase ordering test. The concrete <see cref="CinderTrainer"/>
    /// returns a recognizable identity string so the test can assert that <c>Initialize</c> ran after <c>Inject</c> populated
    /// the trainer reference.
    /// </summary>
    public interface ITrainer
    {
        string Name { get; }
    }

    /// <summary>
    /// Concrete <see cref="ITrainer"/> that returns "Cinder".
    /// Identity-only; carries no dependencies of its own so the two-phase ordering test
    /// stays focused on the Phase 1 / Phase 2 contract.
    /// </summary>
    public sealed class CinderTrainer : ITrainer
    {
        public string Name => "Cinder";
    }

    /// <summary>
    /// Combined fixture for the Phase 1/Phase 2 ordering test. Carries an <c>[Inject]</c> property
    /// (Phase 1) and implements <see cref="IInitializable"/> (Phase 2). Inside <c>Initialize</c>, the fixture
    /// records the trainer's name as observable evidence that Phase 1 completed before Phase 2 ran.
    /// </summary>
    public sealed class TwoPhaseTarget : IInitializable
    {
        [Inject] public ITrainer Trainer { get; private set; }
        public string TrainerNameAtInitialize { get; private set; }

        public void Initialize()
        {
            // By the time this runs (Phase 2), Trainer is populated by Phase 1.
            // Recording its name is sufficient evidence; null would mean the contract is broken
            TrainerNameAtInitialize = Trainer?.Name;
        }
    }
}