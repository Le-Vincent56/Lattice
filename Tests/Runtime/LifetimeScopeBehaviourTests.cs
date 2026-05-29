using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Unity;
using Didionysymus.Lattice.Tests.Runtime.Fixtures;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Didionysymus.Lattice.Tests.Runtime
{
    /// <summary>
    /// Test-only <see cref="LifetimeScopeBehaviour"/> subclass that registers <see cref="IPlayModeService"/>
    /// in its <see cref="Configure"/> override. Used by every test in this fixture so the scene-walk has
    /// at least one resolvable type to inject into the test MonoBehaviours.
    /// </summary>
    public sealed class TestRootScope : LifetimeScopeBehaviour
    {
        protected override void Configure(IContainerBuilder builder)
        {
            builder.Register<IPlayModeService, PlayModeService>(Lifetime.Singleton);
        }
    }

    /// <summary>
    /// Pins the Step 4 PlayMode contract for <see cref="LifetimeScopeBehaviour"/>:
    /// <list type="bullet">
    ///     <item><c>Awake</c> walks the scene's root GameObjects and runs <c>Inject</c> on every <see cref="MonoBehaviour"/> beneath them, before any of them reach <c>Start</c></item>
    ///     <item><see cref="IInitializable.Initialize"/> runs after Phase 1 across the scene completes; injected fields are observable inside <c>Initialize</c></item>
    ///     <item><see cref="UnityResolverExtensions.InjectGameObject"/> populates <c>[Inject]</c> fields after-the-fact for objects spawned post-scene-walk</item>
    ///     <item><see cref="UnityResolverExtensions.InstantiateAndInject{T}"/> spawns and injects in one call so spawned components have populated fields before <c>Start</c></item>
    /// </list>
    /// All fixtures are runtime-created GameObjects (no scene assets, no prefab assets); each <c>[UnityTest]</c>
    /// yields one frame so <c>Awake</c>/<c>Start</c> complete before assertions run.
    /// </summary>
    /// <remarks>
    /// MonoBehaviour callback timing relevant to these tests:
    /// <list type="number">
    ///     <item><c>AddComponent&lt;T&gt;</c> on an active GameObject runs <c>T.Awake</c> synchronously; <c>Start</c> runs at the start of the next frame.</item>
    ///     <item><c>yield return null</c> waits one frame; by that point every component's <c>Awake</c> AND <c>Start</c> have run.</item>
    ///     <item>The scope's <c>[DefaultExecutionOrder(int.MinValue + 100)]</c> guarantees its <c>Awake</c> runs before any user MonoBehaviour <c>Awake</c> in the same frame.</item>
    /// </list>
    /// </remarks>
    [TestFixture]
    public sealed class LifetimeScopeBehaviourTests
    {
        private GameObject _scopeGo;
        private GameObject _consumerGo;
        private Scene _isolatedScene;

        /// <summary>
        /// Creates a fresh isolated scene for each test and sets it active so GameObjects
        /// created via <c>new GameObject()</c> are confined to this test's scene-walk surface.
        /// Required because the production Bootstrap.unity (registered at Build Settings index 0)
        /// is loaded by Unity when entering PlayMode; without isolation, the PlatformProbe
        /// in that scene leaks into every LifetimeScopeBehaviour test's scene-walk
        /// and throws RegistrationNotFoundException for IClock against the test's container.
        /// </summary>
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            _isolatedScene = SceneManager.CreateScene("Test_LifetimeScopeBehaviour_" + Guid.NewGuid().ToString("N"));
            SceneManager.SetActiveScene(_isolatedScene);
            yield return null;
        }

        /// <summary>
        /// Destroys test GameObjects in reverse-creation order and unloads the
        /// isolated test scene.
        /// </summary>
        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            if (_consumerGo) Object.Destroy(_consumerGo);
            if (_scopeGo) Object.Destroy(_scopeGo);

            if (_isolatedScene.IsValid() && _isolatedScene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(_isolatedScene);
            }
            else
            {
                yield return null;
            }
        }

        /// <summary>
        /// When a <see cref="LifetimeScopeBehaviour"/> is added to a scene that already contains an
        /// <see cref="InjectableMonoBehaviour"/>, the consumer's <c>[Inject]</c> field must be populated
        /// before the consumer's <c>Start</c> runs. Verifies the scene-walk in <c>Awake</c> path and
        /// the <c>[DefaultExecutionOrder(int.MinValue + 100)]</c> ordering guarantee.
        /// </summary>
        [UnityTest]
        public IEnumerator Awake_WhenSceneHasInjectableMonoBehaviour_PopulatesInjectFieldBeforeStart()
        {
            // Create the consumer first so it's a scene root by the time the scope's Awake walks the scene.
            // The scope's [DefaultExecutionOrder(int.MinValue + 100)] ensures its Awake runs before the
            // consumer's Awake, even though the consumer was added to the scene first.
            _consumerGo = new GameObject("Consumer");
            InjectableMonoBehaviour consumer = _consumerGo.AddComponent<InjectableMonoBehaviour>();

            _scopeGo = new GameObject("RootScope");
            _scopeGo.AddComponent<TestRootScope>();

            // One frame: by now both objects have completed Awake AND Start.
            yield return null;

            Assert.IsNotNull(consumer.Service,
                "Scope's Awake walked the scene and injected the consumer's [Inject] field");
            Assert.IsFalse(consumer.ServiceWasNullInStart,
                "By Start, the [Inject] field is populated; this is the timing-rule-2 contract");
        }

        /// <summary>
        /// When the scene contains a MonoBehaviour that implements <see cref="IInitializable"/>,
        /// <c>Initialize</c> runs after Phase 1 across the scene completes; injected fields are
        /// observable inside <c>Initialize</c>. Verifies the Phase 2 walk added to
        /// <see cref="LifetimeScopeBehaviour.InjectScene"/> via <see cref="UnityResolverExtensions.InitializeGameObject"/>.
        /// </summary>
        [UnityTest]
        public IEnumerator Awake_WhenSceneHasInitializableMonoBehaviour_InitializeSeesPopulatedField()
        {
            _consumerGo = new GameObject("Consumer");
            InjectableInitializableMonoBehaviour consumer =
                _consumerGo.AddComponent<InjectableInitializableMonoBehaviour>();

            _scopeGo = new GameObject("RootScope");
            _scopeGo.AddComponent<TestRootScope>();

            yield return null;

            Assert.IsTrue(consumer.ServiceWasReadyInInitialize,
                "Initialize ran after Phase 1 bindings completed; Service was non-null when Initialize fired");
        }

        /// <summary>
        /// <see cref="UnityResolverExtensions.InjectGameObject"/> must populate <c>[Inject]</c> fields
        /// on a GameObject that was added to the scene AFTER the scope's scene-walk had already run.
        /// Verifies the after-the-fact injection path used by gameplay code that spawns objects late.
        /// </summary>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenCalledOnLateGameObject_PopulatesInjectFields()
        {
            _scopeGo = new GameObject("RootScope");
            TestRootScope scope = _scopeGo.AddComponent<TestRootScope>();

            // Wait a frame so the scope's Awake (and the empty-of-consumers scene-walk) have completed.
            yield return null;

            // Create the consumer AFTER the scope's scene walk has already run.
            _consumerGo = new GameObject("LateConsumer");
            InjectableMonoBehaviour consumer = _consumerGo.AddComponent<InjectableMonoBehaviour>();

            Assert.IsNull(consumer.Service,
                "Pre-condition: scope's scene walk happened before this consumer was created; field is null until explicit Inject");

            scope.Resolver.InjectGameObject(_consumerGo);

            Assert.IsNotNull(consumer.Service, "InjectGameObject populated the [Inject] field after-the-fact");
        }

        /// <summary>
        /// <see cref="UnityResolverExtensions.InstantiateAndInject{T}"/> must spawn the prefab AND
        /// populate <c>[Inject]</c> fields on the resulting instance. Verifies the spawn-and-inject
        /// helper that gameplay code uses to keep spawned MonoBehaviours' field-populated invariant
        /// before their first <c>Start</c>.
        /// </summary>
        [UnityTest]
        public IEnumerator InstantiateAndInject_WhenSpawningComponent_ReturnsInjectedInstance()
        {
            _scopeGo = new GameObject("RootScope");
            TestRootScope scope = _scopeGo.AddComponent<TestRootScope>();
            yield return null;

            // The "prefab" here is just an active GameObject in the scene used as an Instantiate template.
            // It was created after the scope's scene walk ran (frame 1, post-yield), so its Service is null;
            // we are not testing the prefab itself, only the instance returned by InstantiateAndInject.
            InjectableMonoBehaviour prefab = new GameObject("Prefab").AddComponent<InjectableMonoBehaviour>();
            try
            {
                Assert.IsNull(prefab.Service, "Pre-condition: prefab was never visited by the scope's scene walk");

                InjectableMonoBehaviour instance = scope.Resolver.InstantiateAndInject(prefab);
                _consumerGo = instance.gameObject;

                Assert.IsNotNull(instance.Service,
                    "InstantiateAndInject populated the spawned instance's [Inject] field");
            }
            finally
            {
                // Destroy the template GameObject regardless of test outcome. The instance is destroyed
                // via _consumerGo in TearDown.
                if (prefab) Object.Destroy(prefab.gameObject);
            }
        }
    }
}