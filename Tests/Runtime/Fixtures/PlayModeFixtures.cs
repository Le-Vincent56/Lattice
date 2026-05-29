using System;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Unity;
using UnityEngine;

namespace Didionysymus.Lattice.Tests.Runtime.Fixtures
{
    /// <summary>
    /// Marker service used by PlayMode test fixtures. Defined locally in this assembly so the Unity
    /// test asmdef does not need to cross the Editor-only boundary into <c>Didionysymus.Lattice.Tests</c>; the
    /// shape mirrors the EditMode <c>IServiceA</c> closely enough that PlayMode and EditMode tests share
    /// the same conceptual contract without sharing the actual type.
    /// </summary>
    public interface IPlayModeService
    {
        Guid InstanceID { get; }
    }

    /// <summary>
    /// Concrete <see cref="IPlayModeService"/>; identity-only, no dependencies. Resolved via standard
    /// constructor activation when registered as Singleton in PlayMode test scopes.
    /// </summary>
    public sealed class PlayModeService : IPlayModeService
    {
        public Guid InstanceID { get; } = Guid.NewGuid();
    }

    /// <summary>
    /// PlayMode fixture for the timing-rules contract. Tracks whether the <c>[Inject]</c> field was
    /// non-null when its own <c>Awake</c> ran (informational; per timing rule 1, components must NOT
    /// rely on injected fields in <c>Awake</c>) and when <c>Start</c> ran (the contract: <c>Start</c>
    /// MUST see populated fields when a <see cref="LifetimeScopeBehaviour"/> in the same scene
    /// performed its scene-walk during its own <c>Awake</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// MonoBehaviour timing rules pinned by these fixtures:
    /// <list type="number">
    ///     <item>Components must NOT access <c>[Inject]</c> fields in <c>Awake</c>; the scope's scene-walk may not have reached this object yet.</item>
    ///     <item>By <c>Start</c>, every <c>[Inject]</c> field is populated (assuming a <see cref="LifetimeScopeBehaviour"/> with <c>[DefaultExecutionOrder(int.MinValue + 100)]</c> hosts the same scene).</item>
    ///     <item><see cref="IInitializable.Initialize"/> runs after Phase 1 across the entire scene completes; peers' <c>[Inject]</c> fields are observable too.</item>
    /// </list>
    /// <see cref="ServiceWasNullInAwake"/> is informational only; tests do not assert on it because the
    /// awake-time observation depends on iteration order during the scope's scene-walk, which is not
    /// part of the public contract.
    /// </para>
    /// </remarks>
    public sealed class InjectableMonoBehaviour : MonoBehaviour
    {
        [Inject] public IPlayModeService Service;

        /// <summary>
        /// Captured during this component's own <c>Awake</c>. Informational: per timing rule 1, this is
        /// not a contract guarantee either way. <see cref="ServiceWasNullInStart"/> is the contract assertion.
        /// </summary>
        public bool ServiceWasNullInAwake { get; private set; }

        /// <summary>
        /// Captured during this component's <c>Start</c>. Contract: must be false. By <c>Start</c>, the
        /// <see cref="LifetimeScopeBehaviour"/>'s scene-walk has run and populated <see cref="Service"/>.
        /// </summary>
        public bool ServiceWasNullInStart { get; private set; }

        private void Awake() => ServiceWasNullInAwake = Service == null;

        private void Start() => ServiceWasNullInStart = Service == null;
    }

    /// <summary>
    /// PlayMode fixture combining <c>[Inject]</c> with <see cref="IInitializable"/>. <c>Initialize</c>
    /// records whether <see cref="Service"/> was populated by the time it ran; the contract is that
    /// Phase 2 (<c>Initialize</c>) sees Phase 1 (member injection) complete across every visited
    /// MonoBehaviour in the scene.
    /// </summary>
    public sealed class InjectableInitializableMonoBehaviour : MonoBehaviour, IInitializable
    {
        [Inject] public IPlayModeService Service;

        /// <summary>
        /// Captured during <see cref="Initialize"/>. Contract: must be true. Phase 2 always observes
        /// Phase 1's bindings as complete on visited objects.
        /// </summary>
        public bool ServiceWasReadyInInitialize { get; private set; }

        public void Initialize() => ServiceWasReadyInInitialize = Service != null;
    }
}