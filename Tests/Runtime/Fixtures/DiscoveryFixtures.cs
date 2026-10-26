using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Didionysymus.Lattice.Tests.Runtime.Fixtures
{
    /// <summary>
    /// Component with no members and no interfaces, so discovery gives it neither role and never passes it to
    /// <c>Inject</c>.
    /// </summary>
    public sealed class PlainMonoBehaviour : MonoBehaviour
    {
    }

    /// <summary>
    /// Component with an <c>[Inject]</c> field that counts its <c>Initialize</c> calls and records whether the
    /// field was bound when <c>Initialize</c> ran.
    /// </summary>
    public sealed class BoundCountingComponent : MonoBehaviour, IInitializable
    {
        [Inject] public IPlayModeService Service;
        public int InitializeCount { get; private set; }
        public bool ServiceBoundAtInitialize { get; private set; }

        public void Initialize()
        {
            InitializeCount++;
            ServiceBoundAtInitialize = Service != null;
        }
    }

    /// <summary>
    /// Abstract component that declares a private <c>[Inject]</c> field, a private <c>[Inject]</c> property and a
    /// private <c>[Inject]</c> method, each exposed through a public read-only accessor, so a derived component's
    /// injection members are all inherited.
    /// </summary>
    public abstract class InjectingBaseComponent : MonoBehaviour
    {
        [Inject] private IPlayModeService _fieldService;
        private IPlayModeService _methodService;
        [Inject] private IPlayModeService PropertyService { get; set; }
        public IPlayModeService FieldService => _fieldService;
        public IPlayModeService InjectedProperty => PropertyService;
        public IPlayModeService MethodService => _methodService;

        /// <summary>
        /// Records the injected service for <see cref="MethodService"/>.
        /// </summary>
        /// <param name="service">The service resolved from the injecting scope.</param>
        [Inject]
        private void Construct(IPlayModeService service)
        {
            _methodService = service;
        }
    }

    /// <summary>
    /// Derives from <see cref="InjectingBaseComponent"/> and records in <c>Start</c> whether all three inherited
    /// members were bound by then.
    /// </summary>
    public sealed class DerivedInjectedComponent : InjectingBaseComponent
    {
        public bool AllBoundAtStart { get; private set; }

        /// <summary>
        /// Records whether the inherited field, property and method argument are all bound.
        /// </summary>
        private void Start()
        {
            AllBoundAtStart = FieldService != null && InjectedProperty != null && MethodService != null;
        }
    }

    /// <summary>
    /// <see cref="IObjectResolver"/> that only records each instance passed to <c>Inject</c>, in call order, so a
    /// test can see which components an extension hands to the resolver. Every other member throws
    /// <see cref="NotSupportedException"/>; <c>Dispose</c> does nothing.
    /// </summary>
    public sealed class CountingResolver : IObjectResolver
    {
        public readonly List<object> Injected = new List<object>();

        public T Resolve<T>() => throw new NotSupportedException("counting resolver");
        public object Resolve(Type type) => throw new NotSupportedException("counting resolver");
        public IReadOnlyList<T> ResolveAll<T>() => throw new NotSupportedException("counting resolver");
        public IObjectResolver CreateChildScope(Action<IContainerBuilder> configure) => throw new NotSupportedException("counting resolver");
        public void Inject(object instance) => Injected.Add(instance);

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Service whose <c>Initialize</c> adds a <see cref="BoundCountingComponent"/> to <see cref="Target"/> during
    /// the scope's pass and keeps it in <see cref="Added"/>. Tests reset both statics in <c>[UnitySetUp]</c>.
    /// </summary>
    public sealed class ComponentAddingInitializable : IInitializable
    {
        public static GameObject Target;
        public static BoundCountingComponent Added;

        public void Initialize()
        {
            Added = Target.AddComponent<BoundCountingComponent>();
        }
    }

    /// <summary>
    /// Automatic host that registers <see cref="IPlayModeService"/> and <see cref="ComponentAddingInitializable"/>
    /// as Singletons, so its service pass adds a component beneath a scene root.
    /// </summary>
    public sealed class ComponentAddingHost : LifetimeScopeBehaviour
    {
        protected override void Configure(IContainerBuilder builder)
        {
            builder.Register<IPlayModeService, PlayModeService>(Lifetime.Singleton);
            builder.Register<ComponentAddingInitializable>(Lifetime.Singleton);
        }
    }

    /// <summary>
    /// Service whose <c>Initialize</c> destroys <see cref="Target"/> with <c>Object.DestroyImmediate</c> during the
    /// scope's pass. Tests reset the static in <c>[UnitySetUp]</c>.
    /// </summary>
    public sealed class ComponentDestroyingInitializable : IInitializable
    {
        public static Component Target;

        public void Initialize()
        {
            Object.DestroyImmediate(Target);
        }
    }

    /// <summary>
    /// Automatic host that registers <see cref="IPlayModeService"/> and <see cref="ComponentDestroyingInitializable"/>
    /// as Singletons, so its service pass destroys a component it already bound.
    /// </summary>
    public sealed class ComponentDestroyingHost : LifetimeScopeBehaviour
    {
        protected override void Configure(IContainerBuilder builder)
        {
            builder.Register<IPlayModeService, PlayModeService>(Lifetime.Singleton);
            builder.Register<ComponentDestroyingInitializable>(Lifetime.Singleton);
        }
    }

    /// <summary>
    /// Component whose <c>Initialize</c> counts the call, then calls <c>InitializeGameObject</c> on
    /// <see cref="OtherRoot"/> with <see cref="Resolver"/>, so a walk runs nested inside another walk's use.
    /// </summary>
    public sealed class NestedInitializingComponent : MonoBehaviour, IInitializable
    {
        public IObjectResolver Resolver;
        public GameObject OtherRoot;
        public int InitializeCount { get; private set; }

        public void Initialize()
        {
            InitializeCount++;
            Resolver.InitializeGameObject(OtherRoot);
        }
    }

    /// <summary>
    /// Component whose <c>[Inject]</c> method records its service, then calls <c>InjectGameObject</c> on
    /// <see cref="OtherRoot"/> with <see cref="Resolver"/> when one is set, so a bind runs nested inside another
    /// bind.
    /// </summary>
    public sealed class NestedInjectingComponent : MonoBehaviour
    {
        public IObjectResolver Resolver;
        public GameObject OtherRoot;
        public IPlayModeService Service { get; private set; }

        /// <summary>
        /// Records <paramref name="service"/>, then binds <see cref="OtherRoot"/> when <see cref="Resolver"/> is set.
        /// </summary>
        /// <param name="service">The service resolved from the injecting scope.</param>
        [Inject]
        public void Construct(IPlayModeService service)
        {
            Service = service;
            if (Resolver != null) Resolver.InjectGameObject(OtherRoot);
        }
    }

    /// <summary>
    /// Per-test bed for the discovery, boundary and ownership tests: creates a fresh active scene, tracks every
    /// GameObject, resolver and extra scene a test creates, and tears them down in the order the explicit host
    /// tests use. It never calls a host member.
    /// </summary>
    public sealed class SceneTestBed
    {
        private readonly List<GameObject> _gameObjects = new List<GameObject>();
        private readonly List<IObjectResolver> _resolvers = new List<IObjectResolver>();
        private readonly List<Scene> _scenes = new List<Scene>();

        /// <summary>
        /// The scene <see cref="SetUp"/> created and made active; every GameObject <see cref="Create"/> makes lands in it.
        /// </summary>
        public Scene Scene { get; private set; }

        /// <summary>
        /// Creates a scene named <paramref name="prefix"/> plus a unique suffix, makes it active and tracks it, so
        /// objects from any other loaded scene stay out of a host's batch; then waits one frame.
        /// </summary>
        /// <param name="prefix">The start of the scene's name.</param>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        public IEnumerator SetUp(string prefix)
        {
            Scene = SceneManager.CreateScene(prefix + "_" + Guid.NewGuid().ToString("N"));
            _scenes.Add(Scene);
            SceneManager.SetActiveScene(Scene);
            yield return null;
        }

        /// <summary>
        /// Destroys every tracked GameObject still alive, waits a frame, disposes every tracked resolver (newest
        /// first), then unloads every tracked scene still loaded.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        public IEnumerator TearDown()
        {
            for (int i = 0; i < _gameObjects.Count; i++)
            {
                if (_gameObjects[i]) Object.Destroy(_gameObjects[i]);
            }

            yield return null;

            for (int i = _resolvers.Count - 1; i >= 0; i--)
            {
                try
                {
                    _resolvers[i].Dispose();
                }
                catch (AggregateException)
                {
                    // A test's own throwing disposables; the test asserted on them already.
                }
            }

            for (int i = 0; i < _scenes.Count; i++)
            {
                Scene scene = _scenes[i];
                if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
            }

            _gameObjects.Clear();
            _resolvers.Clear();
            _scenes.Clear();
        }

        /// <summary>
        /// Creates a tracked GameObject in the active scene, parented under <paramref name="parent"/> when one is given.
        /// </summary>
        /// <param name="name">The GameObject's name.</param>
        /// <param name="parent">The parent GameObject; null for a scene root.</param>
        /// <returns>The new GameObject.</returns>
        public GameObject Create(string name, GameObject parent)
        {
            GameObject gameObject = new GameObject(name);
            _gameObjects.Add(gameObject);
            if (parent != null) gameObject.transform.SetParent(parent.transform, false);
            return gameObject;
        }

        /// <summary>
        /// Creates a tracked GameObject as <see cref="Create"/> does and adds a <typeparamref name="T"/> to it.
        /// </summary>
        /// <typeparam name="T">The component type to add.</typeparam>
        /// <param name="name">The GameObject's name.</param>
        /// <param name="parent">The parent GameObject; null for a scene root.</param>
        /// <returns>The new component.</returns>
        public T Add<T>(string name, GameObject parent) where T : Component
        {
            return Create(name, parent).AddComponent<T>();
        }

        /// <summary>
        /// Tracks <paramref name="resolver"/> so <see cref="TearDown"/> disposes it.
        /// </summary>
        /// <param name="resolver">The resolver to track.</param>
        /// <returns><paramref name="resolver"/>.</returns>
        public IObjectResolver Track(IObjectResolver resolver)
        {
            _resolvers.Add(resolver);
            return resolver;
        }

        /// <summary>
        /// Creates and tracks a second scene named <paramref name="prefix"/> plus a unique suffix, without making it
        /// active.
        /// </summary>
        /// <param name="prefix">The start of the scene's name.</param>
        /// <returns>The new scene.</returns>
        public Scene CreateExtraScene(string prefix)
        {
            Scene scene = SceneManager.CreateScene(prefix + "_" + Guid.NewGuid().ToString("N"));
            _scenes.Add(scene);
            return scene;
        }

        /// <summary>
        /// Yields frames until <paramref name="task"/> completes or 10 s of real time pass; never blocks on it.
        /// </summary>
        /// <param name="task">The task to wait for.</param>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        public static IEnumerator WaitFor(Task task)
        {
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
        }
    }
}
