using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Exceptions;
using Didionysymus.Lattice.Runtime.Unity;
using Didionysymus.Lattice.Tests.Runtime.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Didionysymus.Lattice.Tests.Runtime
{
    /// <summary>
    /// Pins the PlayMode contract of <see cref="ExplicitScopeHost"/>:
    /// <list type="number">
    ///     <item>Nothing happens on its own: a host nobody attaches stays detached and leaves its scene unbound</item>
    ///     <item><c>Attach</c> accepts a built-in resolver while the host is detached, before or after <c>Awake</c>, and refuses a null, second, disposed, stopping or busy resolver and a destroyed host</item>
    ///     <item>An owned resolver is shut down by the host; a borrowed one never is</item>
    ///     <item><c>PrepareAsync</c> binds the batch (the scene's roots, or the given roots), runs the scope's pass, initializes the batch's components once, then runs the scope's startup; it activates nothing</item>
    ///     <item>The host becomes ready only when every step succeeded; a failure faults it, a cancellation after preparation began faults it and cancels readiness</item>
    ///     <item><c>WaitUntilReadyAsync</c> serves any number of callers and is cancelled by a shutdown before ready or by the caller's own token</item>
    ///     <item><c>ShutdownAsync</c> cancels a running prepare, waits for it to settle, shuts an owned resolver down, clears <c>Resolver</c> and detaches; it reports cleanup failures in one aggregate and refuses a call from inside the host's own startup</item>
    ///     <item><c>OnDestroy</c> starts the same shutdown without waiting, logs each failure, and disposes at once when destroyed inside its own startup</item>
    ///     <item>Every public method refuses a caller off Unity's main thread</item>
    ///     <item>Objects in the scene receive <c>Awake</c> before preparation; their fields are bound only once <c>PrepareAsync</c> bound them</item>
    /// </list>
    /// Every scene is runtime-created (<c>SceneManager.CreateScene</c>), standing in for an additively loaded scene;
    /// there are no scene or prefab assets. Tasks are driven by yielding frames until they complete or 10 s of real
    /// time pass; no test blocks on a task.
    /// </summary>
    /// <remarks>
    /// Exercises <see cref="ExplicitScopeHost"/>, <see cref="ScopeOwnership"/>, <see cref="ScopeHostState"/> and,
    /// through the host, the shared host engine and the core's pass, startup and shutdown.
    /// </remarks>
    [TestFixture]
    public sealed class ExplicitScopeHostTests
    {
        private readonly List<GameObject> _gameObjects = new List<GameObject>();
        private readonly List<IObjectResolver> _resolvers = new List<IObjectResolver>();
        private readonly List<Scene> _scenes = new List<Scene>();
        private Scene _regionScene;

        /// <summary>
        /// Clears the event log, then creates a fresh region scene and makes it active, so every GameObject a test
        /// creates lands in it and objects from any other loaded scene stay out of the host's batch.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            HostEventLog.Events.Clear();
            _regionScene = SceneManager.CreateScene("Region_" + Guid.NewGuid().ToString("N"));
            _scenes.Add(_regionScene);
            SceneManager.SetActiveScene(_regionScene);
            yield return null;
        }

        /// <summary>
        /// Destroys every tracked GameObject still alive, waits a frame, disposes every tracked resolver (children
        /// first), then unloads every tracked scene still loaded. It never calls a host member.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTearDown]
        public IEnumerator UnityTearDown()
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
        /// A null resolver is refused and the host stays detached.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Attach_WhenResolverIsNull_ThrowsArgumentNullException()
        {
            ExplicitScopeHost host = CreateHost();

            Assert.Throws<ArgumentNullException>(() => host.Attach(null, ScopeOwnership.Owned), "A null resolver is refused");

            Assert.AreEqual(ScopeHostState.Detached, host.State, "The host stays detached");
            yield return null;
        }

        /// <summary>
        /// A second attach is refused and the first resolver stays attached.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Attach_WhenAlreadyAttached_ThrowsInvalidOperationException()
        {
            ExplicitScopeHost host = CreateHost();
            IObjectResolver boot = BuildBoot();
            IObjectResolver region = CreateRegion(boot, null);
            host.Attach(region, ScopeOwnership.Owned);
            IObjectResolver second = CreateRegion(boot, null);

            Assert.Throws<InvalidOperationException>(() => host.Attach(second, ScopeOwnership.Owned), "A second attach is refused");

            Assert.AreSame(region, host.Resolver, "The first resolver stays attached");
            yield return null;
        }

        /// <summary>
        /// A disposed resolver is refused and the host stays detached.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Attach_WhenResolverDisposed_ThrowsObjectDisposedException()
        {
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), null);
            region.Dispose();

            Assert.Throws<ObjectDisposedException>(() => host.Attach(region, ScopeOwnership.Owned), "A disposed resolver is refused");

            Assert.AreEqual(ScopeHostState.Detached, host.State, "The host stays detached");
            yield return null;
        }

        /// <summary>
        /// A destroyed host refuses an attach and leaves the resolver undisposed.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Attach_WhenHostDestroyed_ThrowsInvalidOperationException()
        {
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), null);
            Object.Destroy(host.gameObject);
            yield return null;

            Assert.Throws<InvalidOperationException>(() => host.Attach(region, ScopeOwnership.Owned), "A destroyed host refuses an attach");

            Assert.AreNotEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The refused resolver is not disposed");
        }

        /// <summary>
        /// An attach after <c>Awake</c> has run attaches the resolver and prepares nothing.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Attach_WhenCalledAfterAwake_AttachesWithoutPreparing()
        {
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), null);
            yield return null;

            host.Attach(region, ScopeOwnership.Owned);
            yield return null;

            Assert.AreEqual(ScopeHostState.Attached, host.State, "The host is attached");
            Assert.AreSame(region, host.Resolver, "The resolver is exposed");
            Assert.IsNull(component.Service, "Attaching binds nothing");
            Assert.AreEqual(0, component.InitializeCount, "Attaching initializes nothing");
            Assert.AreEqual(LifecycleState.Created, region.GetLifecycleState(), "Attaching runs no pass");
        }

        /// <summary>
        /// A host nobody attaches stays detached over several frames and leaves its scene unbound and uninitialized.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator State_WhenHostNeverAttached_StaysDetachedAndLeavesSceneUnbound()
        {
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();

            yield return null;
            yield return null;
            yield return null;

            Assert.AreEqual(ScopeHostState.Detached, host.State, "The host stays detached");
            Assert.IsNull(host.Resolver, "No resolver is built");
            Assert.IsNull(component.Service, "Nothing is bound");
            Assert.AreEqual(0, component.InitializeCount, "Nothing is initialized");
        }

        /// <summary>
        /// Components in a loaded, active scene stay unbound until <c>PrepareAsync</c> is called; then they are bound
        /// and initialized and the host is ready.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenHostLoadedActive_LeavesComponentsUnboundUntilCalled()
        {
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), null);
            yield return null;
            yield return null;
            bool unboundBeforePrepare = component.Service == null;
            int initializedBeforePrepare = component.InitializeCount;
            host.Attach(region, ScopeOwnership.Owned);
            yield return null;

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);

            Assert.IsTrue(unboundBeforePrepare, "Before preparation the field is unbound");
            Assert.AreEqual(0, initializedBeforePrepare, "Before preparation nothing is initialized");
            Assert.IsTrue(component.ServiceWasNullInAwake, "Awake ran before preparation and saw no service");
            Assert.AreEqual(TaskStatus.RanToCompletion, prepare.Status, "The prepare completes");
            Assert.IsNotNull(component.Service, "Preparation binds the component");
            Assert.AreEqual(1, component.InitializeCount, "Preparation initializes the component once");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The host is ready");
        }

        /// <summary>
        /// Preparation initializes the scope's services, then the scene's components, then runs the scope's startup.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenAttached_InitializesServicesThenComponentsThenStarts()
        {
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => new LoggedStartable(), Lifetime.Scoped));
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);

            CollectionAssert.AreEqual(new[] { "service:init", "component:init", "startable:start" }, HostEventLog.Events, "Services, then components, then startup");
            Assert.IsTrue(component.ServiceInitializedAtInitialize, "A component's Initialize sees an initialized service");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The host is ready");
            Assert.AreEqual(LifecycleState.Ready, region.GetLifecycleState(), "The scope is ready");
        }

        /// <summary>
        /// With an explicit batch, only the given roots are prepared.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenRootsGiven_PreparesOnlyThoseRoots()
        {
            RegionComponent a = CreateComponent("A");
            RegionComponent b = CreateComponent("B");
            ExplicitScopeHost host = CreateHost();
            host.Attach(CreateRegion(BuildBoot(), null), ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(new[] { a.gameObject }, CancellationToken.None);
            yield return WaitFor(prepare);

            Assert.IsNotNull(a.Service, "The given root is bound");
            Assert.AreEqual(1, a.InitializeCount, "The given root is initialized once");
            Assert.IsNull(b.Service, "A root outside the batch is not bound");
            Assert.AreEqual(0, b.InitializeCount, "A root outside the batch is not initialized");
        }

        /// <summary>
        /// An inactive root is bound and initialized without being activated; once activated, its <c>Awake</c> sees
        /// the injected field.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenRootInactive_PreparesWithoutActivatingAndAwakeSeesInjectedField()
        {
            GameObject inactive = new GameObject("Inactive");
            _gameObjects.Add(inactive);
            inactive.SetActive(false);
            RegionComponent c = inactive.AddComponent<RegionComponent>();
            ExplicitScopeHost host = CreateHost();
            host.Attach(CreateRegion(BuildBoot(), null), ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);
            bool boundAfterPrepare = c.Service != null;
            int initializedAfterPrepare = c.InitializeCount;
            int awakeAfterPrepare = c.AwakeCount;
            bool activeAfterPrepare = c.gameObject.activeSelf;
            c.gameObject.SetActive(true);

            Assert.IsTrue(boundAfterPrepare, "The inactive component is bound");
            Assert.AreEqual(1, initializedAfterPrepare, "The inactive component is initialized once");
            Assert.AreEqual(0, awakeAfterPrepare, "Preparation does not wake it");
            Assert.IsFalse(activeAfterPrepare, "Preparation activates nothing");
            Assert.AreEqual(1, c.AwakeCount, "Activation runs Awake");
            Assert.IsFalse(c.ServiceWasNullInAwake, "Awake sees the injected field");
        }

        /// <summary>
        /// A prepare after the host is ready prepares nothing more.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenCalledAfterReady_PreparesNothingMore()
        {
            LoggedStartable s = new LoggedStartable();
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            host.Attach(CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => s, Lifetime.Scoped)), ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);

            Task again = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(again);

            Assert.AreEqual(TaskStatus.RanToCompletion, again.Status, "The second prepare completes");
            Assert.AreEqual(1, component.InitializeCount, "The component is initialized once");
            Assert.AreEqual(1, s.StartCount, "The startable starts once");
        }

        /// <summary>
        /// A prepare while one runs is refused; the running prepare completes and its participant starts once.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenCalledWhilePreparing_FaultsWithInvalidOperationException()
        {
            PlayModeGatedStartable g = new PlayModeGatedStartable();
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            host.Attach(CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => g, Lifetime.Scoped)), ScopeOwnership.Owned);
            Task first = host.PrepareAsync(CancellationToken.None);
            yield return null;

            Task second = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(second);
            g.Gate.SetResult(true);
            yield return WaitFor(first);

            Assert.IsTrue(second.IsFaulted, "The second prepare faults");
            Assert.IsInstanceOf<InvalidOperationException>(second.Exception.InnerException, "It is refused");
            Assert.AreEqual("PrepareAsync is already running on this host; await WaitUntilReadyAsync instead.", second.Exception.InnerException.Message, "The refusal points to WaitUntilReadyAsync");
            Assert.AreEqual(1, g.StartCount, "The participant starts once");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The running prepare completes");
        }

        /// <summary>
        /// A prepare on a detached host is refused.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenDetached_FaultsWithInvalidOperationException()
        {
            ExplicitScopeHost host = CreateHost();

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<InvalidOperationException>(prepare.Exception.InnerException, "It is refused");
            Assert.AreEqual("Attach a resolver before PrepareAsync.", prepare.Exception.InnerException.Message, "The refusal asks for an attach");
            Assert.AreEqual(ScopeHostState.Detached, host.State, "The host stays detached");
        }

        /// <summary>
        /// A token already cancelled at the call changes nothing; a later prepare succeeds.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenTokenCanceledBeforeCall_StaysAttachedAndPreparesLater()
        {
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            host.Attach(CreateRegion(BuildBoot(), null), ScopeOwnership.Owned);
            using CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();

            Task first = host.PrepareAsync(cts.Token);
            yield return WaitFor(first);
            ScopeHostState stateAfterFirst = host.State;
            bool unboundAfterFirst = component.Service == null;
            Task second = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(second);

            Assert.IsTrue(first.IsCanceled, "The first prepare is cancelled");
            Assert.AreEqual(ScopeHostState.Attached, stateAfterFirst, "The host stays attached");
            Assert.IsTrue(unboundAfterFirst, "Nothing is bound");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The later prepare makes the host ready");
            Assert.IsNotNull(component.Service, "The later prepare binds the component");
        }

        /// <summary>
        /// A service pass that throws faults the host, the prepare and every readiness wait with that exception, and
        /// a later prepare is refused quoting it.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenServicePassThrows_FaultsHostAndReadiness()
        {
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            host.Attach(CreateRegion(BuildBoot(), b => b.Register<ThrowingRegionInitializable>(Lifetime.Scoped)), ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);
            Task ready = host.WaitUntilReadyAsync(CancellationToken.None);
            yield return WaitFor(ready);
            Task again = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(again);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<LifecycleException>(prepare.Exception.InnerException, "The pass failure is reported");
            Assert.IsInstanceOf<NotSupportedException>(prepare.Exception.InnerException.InnerException, "It carries the service's exception");
            Assert.AreEqual("region initialize failed", prepare.Exception.InnerException.InnerException.Message, "It is the service's message");
            Assert.AreEqual(ScopeHostState.Faulted, host.State, "The host is faulted");
            Assert.IsTrue(ready.IsFaulted, "Readiness faults");
            Assert.AreSame(prepare.Exception.InnerException, ready.Exception.InnerException, "Readiness faults with the same exception");
            Assert.AreEqual(0, component.InitializeCount, "No component is initialized");
            Assert.IsTrue(again.IsFaulted, "A later prepare faults");
            Assert.IsInstanceOf<InvalidOperationException>(again.Exception.InnerException, "It is refused");
            StringAssert.StartsWith("This host is faulted and must be shut down before it is attached to a new scope: ", again.Exception.InnerException.Message, "The refusal says to shut down");
            Assert.AreSame(prepare.Exception.InnerException, again.Exception.InnerException.InnerException, "The refusal carries the fault");
        }

        /// <summary>
        /// A startup that throws faults the host and every readiness wait with that exception; the components were
        /// initialized before startup.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenStartupThrows_FaultsHostAndReadiness()
        {
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => new ThrowingRegionStartable(), Lifetime.Scoped));
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);
            Task ready = host.WaitUntilReadyAsync(CancellationToken.None);
            yield return WaitFor(ready);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<LifecycleException>(prepare.Exception.InnerException, "The startup failure is reported");
            Assert.AreEqual(LifecyclePhase.Start, ((LifecycleException)prepare.Exception.InnerException).Phase, "It failed in startup");
            Assert.AreEqual(ScopeHostState.Faulted, host.State, "The host is faulted");
            Assert.IsTrue(ready.IsFaulted, "Readiness faults");
            Assert.AreSame(prepare.Exception.InnerException, ready.Exception.InnerException, "Readiness faults with the same exception");
            Assert.AreEqual(LifecycleState.Faulted, region.GetLifecycleState(), "The scope is faulted");
            Assert.AreEqual(1, component.InitializeCount, "Components initialize before startup");
        }

        /// <summary>
        /// A bind that throws faults the host and every readiness wait with that exception; the scope's pass never
        /// ran, so the scope is not faulted.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenBindThrows_FaultsHostAndReadiness()
        {
            GameObject missing = new GameObject("Missing");
            _gameObjects.Add(missing);
            missing.AddComponent<MissingDependencyComponent>();
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), null);
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);
            Task ready = host.WaitUntilReadyAsync(CancellationToken.None);
            yield return WaitFor(ready);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<RegistrationNotFoundException>(prepare.Exception.InnerException, "The bind failure is reported");
            Assert.AreEqual(ScopeHostState.Faulted, host.State, "The host is faulted");
            Assert.IsTrue(ready.IsFaulted, "Readiness faults");
            Assert.AreSame(prepare.Exception.InnerException, ready.Exception.InnerException, "Readiness faults with the same exception");
            Assert.AreEqual(LifecycleState.Created, region.GetLifecycleState(), "The pass never ran; the scope is not faulted");
        }

        /// <summary>
        /// The caller's token cancelled while startup runs cancels the participant's token; the host faults, the
        /// prepare and readiness are cancelled, and a later prepare is refused saying the preparation was cancelled.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenTokenCanceledDuringStartup_FaultsHostAndCancelsReadiness()
        {
            PlayModeGatedStartable g = new PlayModeGatedStartable();
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => g, Lifetime.Scoped));
            host.Attach(region, ScopeOwnership.Owned);
            using CancellationTokenSource cts = new CancellationTokenSource();
            Task prepare = host.PrepareAsync(cts.Token);
            yield return null;

            cts.Cancel();
            yield return null;
            bool tokenCanceled = g.ReceivedToken.IsCancellationRequested;
            g.Gate.SetResult(true);
            yield return WaitFor(prepare);
            Task ready = host.WaitUntilReadyAsync(CancellationToken.None);
            yield return WaitFor(ready);
            Task again = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(again);

            Assert.IsTrue(tokenCanceled, "The participant's token is cancelled");
            Assert.IsTrue(prepare.IsCanceled, "The prepare is cancelled");
            Assert.AreEqual(ScopeHostState.Faulted, host.State, "The host is faulted");
            Assert.IsTrue(ready.IsCanceled, "Readiness is cancelled");
            Assert.AreEqual(LifecycleState.Faulted, region.GetLifecycleState(), "The scope is faulted");
            Assert.IsTrue(again.IsFaulted, "A later prepare faults");
            Assert.AreEqual("This host's preparation was cancelled; shut it down before attaching a new scope.", again.Exception.InnerException.Message, "The refusal says the preparation was cancelled");
            Assert.IsInstanceOf<OperationCanceledException>(again.Exception.InnerException.InnerException, "The refusal carries the cancellation");
        }

        /// <summary>
        /// Several callers waiting for readiness all complete when the host becomes ready.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator WaitUntilReadyAsync_WhenSeveralCallersWait_AllCompleteOnReady()
        {
            PlayModeGatedStartable g = new PlayModeGatedStartable();
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            host.Attach(CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => g, Lifetime.Scoped)), ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);

            Task w1 = host.WaitUntilReadyAsync(CancellationToken.None);
            Task w2 = host.WaitUntilReadyAsync(CancellationToken.None);
            yield return null;
            bool incompleteBeforeReady = !w1.IsCompleted && !w2.IsCompleted;
            g.Gate.SetResult(true);
            yield return WaitFor(w1);
            yield return WaitFor(w2);
            yield return WaitFor(prepare);

            Assert.IsTrue(incompleteBeforeReady, "Both waits run until the host is ready");
            Assert.AreEqual(TaskStatus.RanToCompletion, w1.Status, "The first wait completes");
            Assert.AreEqual(TaskStatus.RanToCompletion, w2.Status, "The second wait completes");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The host is ready");
        }

        /// <summary>
        /// A caller's own token cancels only that caller's wait; another wait completes when the host is ready.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator WaitUntilReadyAsync_WhenCallerTokenCanceled_CancelsOnlyThatWait()
        {
            PlayModeGatedStartable g = new PlayModeGatedStartable();
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            host.Attach(CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => g, Lifetime.Scoped)), ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            using CancellationTokenSource cts = new CancellationTokenSource();
            Task w1 = host.WaitUntilReadyAsync(cts.Token);
            Task w2 = host.WaitUntilReadyAsync(CancellationToken.None);

            cts.Cancel();
            yield return WaitFor(w1);
            bool w1Canceled = w1.IsCanceled;
            bool w2IncompleteThen = !w2.IsCompleted;
            ScopeHostState stateThen = host.State;
            g.Gate.SetResult(true);
            yield return WaitFor(w2);
            yield return WaitFor(prepare);

            Assert.IsTrue(w1Canceled, "The cancelled caller's wait is cancelled");
            Assert.IsTrue(w2IncompleteThen, "The other wait keeps waiting");
            Assert.AreEqual(ScopeHostState.Preparing, stateThen, "The host is still preparing");
            Assert.AreEqual(TaskStatus.RanToCompletion, w2.Status, "The other wait completes on ready");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The host is ready");
        }

        /// <summary>
        /// Waiting for readiness on a detached host is refused.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator WaitUntilReadyAsync_WhenDetached_FaultsWithInvalidOperationException()
        {
            ExplicitScopeHost host = CreateHost();

            Task wait = host.WaitUntilReadyAsync(CancellationToken.None);
            yield return WaitFor(wait);

            Assert.IsTrue(wait.IsFaulted, "The wait faults");
            Assert.IsInstanceOf<InvalidOperationException>(wait.Exception.InnerException, "It is refused");
            Assert.AreEqual("Attach a resolver before WaitUntilReadyAsync.", wait.Exception.InnerException.Message, "The refusal asks for an attach");
        }

        /// <summary>
        /// A shutdown before the host is ready cancels the readiness wait and the prepare, shuts the owned resolver
        /// down and detaches.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator WaitUntilReadyAsync_WhenShutDownBeforeReady_IsCanceled()
        {
            PlayModeGatedStartable g = new PlayModeGatedStartable();
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => g, Lifetime.Scoped));
            host.Attach(region, ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            Task wait = host.WaitUntilReadyAsync(CancellationToken.None);

            Task shutdown = host.ShutdownAsync(CancellationToken.None);
            yield return WaitFor(wait);
            g.Gate.SetResult(true);
            yield return WaitFor(shutdown);
            yield return WaitFor(prepare);

            Assert.IsTrue(wait.IsCanceled, "The readiness wait is cancelled");
            Assert.IsTrue(prepare.IsCanceled, "The prepare is cancelled");
            Assert.AreEqual(TaskStatus.RanToCompletion, shutdown.Status, "The shutdown completes");
            Assert.AreEqual(ScopeHostState.Detached, host.State, "The host is detached");
            Assert.AreEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The owned resolver is shut down");
        }

        /// <summary>
        /// Waiting for readiness from inside the host's own startup is refused; the host still becomes ready.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator WaitUntilReadyAsync_WhenCalledFromInsideStartAsync_FaultsWithInvalidOperationException()
        {
            ExplicitScopeHost host = CreateHost();
            HostCallingStartable caller = new HostCallingStartable { Target = host, CallWaitUntilReady = true };
            host.Attach(CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => caller, Lifetime.Scoped)), ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);
            yield return WaitFor(caller.ReadyTask);

            Assert.IsTrue(caller.ReadyTask.IsFaulted, "The wait faults");
            Assert.IsInstanceOf<InvalidOperationException>(caller.ReadyTask.Exception.InnerException, "It is refused");
            Assert.AreEqual("WaitUntilReadyAsync cannot wait for a startup it is called from.", caller.ReadyTask.Exception.InnerException.Message, "The refusal names the startup");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The host still becomes ready");
        }

        /// <summary>
        /// Shutting an owned host down shuts its resolver down, disposes the region's services, clears
        /// <c>Resolver</c> and leaves the boot scope and its services alive.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator ShutdownAsync_WhenOwned_ShutsResolverDownAndClearsResolver()
        {
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver boot = BuildBoot();
            IObjectResolver region = CreateRegion(boot, null);
            host.Attach(region, ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);
            IRegionService svc = component.Service;

            Task shutdown = host.ShutdownAsync(CancellationToken.None);
            yield return WaitFor(shutdown);

            Assert.AreEqual(TaskStatus.RanToCompletion, shutdown.Status, "The shutdown completes");
            Assert.AreEqual(ScopeHostState.Detached, host.State, "The host is detached");
            Assert.IsNull(host.Resolver, "Resolver is cleared");
            Assert.AreEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The owned resolver is shut down");
            Assert.AreEqual(1, svc.DisposeCount, "The region's service is disposed once");
            Assert.AreEqual(LifecycleState.Ready, boot.GetLifecycleState(), "The boot scope stays ready");
            Assert.AreSame(svc.Boot, boot.Resolve<IBootService>(), "The region used the boot scope's service");
            Assert.AreEqual(0, svc.Boot.DisposeCount, "The boot scope's service is not disposed");
        }

        /// <summary>
        /// Shutting a borrowed host down detaches it and leaves its resolver and services undisposed.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator ShutdownAsync_WhenBorrowed_LeavesResolverUndisposed()
        {
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), null);
            host.Attach(region, ScopeOwnership.Borrowed);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);

            Task shutdown = host.ShutdownAsync(CancellationToken.None);
            yield return WaitFor(shutdown);

            Assert.AreEqual(ScopeHostState.Detached, host.State, "The host is detached");
            Assert.IsNull(host.Resolver, "Resolver is cleared");
            Assert.AreEqual(LifecycleState.Ready, region.GetLifecycleState(), "The borrowed resolver is left as it was");
            Assert.AreEqual(0, component.Service.DisposeCount, "Its services are not disposed");
        }

        /// <summary>
        /// A shutdown during a running prepare cancels the startup at once, but disposes nothing until the running
        /// participant returns.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator ShutdownAsync_WhenPrepareRunning_CancelsStartupAndDisposesAfterParticipantReturns()
        {
            PlayModeGatedStartable g = new PlayModeGatedStartable();
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => g, Lifetime.Scoped));
            host.Attach(region, ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return null;

            Task shutdown = host.ShutdownAsync(CancellationToken.None);
            yield return null;
            yield return null;
            bool tokenCanceled = g.ReceivedToken.IsCancellationRequested;
            bool completedBeforeReturn = shutdown.IsCompleted;
            LifecycleState stateBeforeReturn = region.GetLifecycleState();
            g.Gate.SetResult(true);
            yield return WaitFor(shutdown);
            yield return WaitFor(prepare);

            Assert.IsTrue(tokenCanceled, "The participant's token is cancelled at once");
            Assert.IsFalse(completedBeforeReturn, "The shutdown waits for the running participant");
            Assert.AreNotEqual(LifecycleState.Disposed, stateBeforeReturn, "Nothing is disposed while it runs");
            Assert.AreEqual(TaskStatus.RanToCompletion, shutdown.Status, "The shutdown completes");
            Assert.AreEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The owned resolver is disposed afterwards");
            Assert.IsTrue(prepare.IsCanceled, "The prepare is cancelled");
            Assert.AreEqual(ScopeHostState.Detached, host.State, "The host is detached");
        }

        /// <summary>
        /// When an owned resolver's disposal throws, the shutdown faults with one aggregate listing each failure in
        /// disposal order, the resolver is still shut down and the host detaches; nothing is logged.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator ShutdownAsync_WhenOwnedDisposalThrows_FaultsWithAggregateExceptionAndDetaches()
        {
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), RegisterThrowingDisposables);
            host.Attach(region, ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);
            region.Resolve<FirstThrowingDisposable>();
            region.Resolve<SecondThrowingDisposable>();

            Task shutdown = host.ShutdownAsync(CancellationToken.None);
            yield return WaitFor(shutdown);

            Assert.IsTrue(shutdown.IsFaulted, "The shutdown faults");
            Assert.IsInstanceOf<AggregateException>(shutdown.Exception.InnerException, "It reports one aggregate");
            AggregateException aggregate = (AggregateException)shutdown.Exception.InnerException;
            StringAssert.StartsWith("One or more objects threw while the host shut its scope down; the scope was still shut down.", aggregate.Message, "The aggregate carries the host's message");
            CollectionAssert.AreEqual(new[] { "second dispose failed", "first dispose failed" }, aggregate.InnerExceptions.Select(e => e.Message).ToArray(), "Each failure is listed in disposal order");
            Assert.AreEqual(ScopeHostState.Detached, host.State, "The host is detached");
            Assert.AreEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The resolver is still shut down");
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>
        /// Shutting a detached host down completes at once.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator ShutdownAsync_WhenDetached_CompletesAtOnce()
        {
            ExplicitScopeHost host = CreateHost();

            Task shutdown = host.ShutdownAsync(CancellationToken.None);
            bool completedAtCall = shutdown.IsCompleted;
            yield return null;

            Assert.IsTrue(completedAtCall, "The shutdown completes at the call");
            Assert.IsFalse(shutdown.IsFaulted, "It does not fault");
        }

        /// <summary>
        /// Two shutdowns during a running prepare share one shutdown: both complete after it, and the region's
        /// service is disposed once.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator ShutdownAsync_WhenCalledTwice_BothCompleteAfterOneShutdown()
        {
            PlayModeGatedStartable g = new PlayModeGatedStartable();
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            host.Attach(CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => g, Lifetime.Scoped)), ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);

            Task s1 = host.ShutdownAsync(CancellationToken.None);
            Task s2 = host.ShutdownAsync(CancellationToken.None);
            yield return null;
            bool incompleteBeforeReturn = !s1.IsCompleted && !s2.IsCompleted;
            g.Gate.SetResult(true);
            yield return WaitFor(s1);
            yield return WaitFor(s2);
            yield return WaitFor(prepare);

            Assert.IsTrue(incompleteBeforeReturn, "Both wait for the running participant");
            Assert.AreEqual(TaskStatus.RanToCompletion, s1.Status, "The first shutdown completes");
            Assert.AreEqual(TaskStatus.RanToCompletion, s2.Status, "The joining shutdown completes");
            Assert.AreEqual(1, component.Service.DisposeCount, "The region's service is disposed once");
            Assert.AreEqual(ScopeHostState.Detached, host.State, "The host is detached");
        }

        /// <summary>
        /// A shutdown from inside the host's own startup is refused and changes nothing.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator ShutdownAsync_WhenCalledFromInsideStartAsync_FaultsWithInvalidOperationException()
        {
            ExplicitScopeHost host = CreateHost();
            HostCallingStartable caller = new HostCallingStartable { Target = host, CallShutdown = true };
            IObjectResolver region = CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => caller, Lifetime.Scoped));
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);
            yield return WaitFor(caller.ShutdownTask);

            Assert.IsTrue(caller.ShutdownTask.IsFaulted, "The shutdown faults");
            Assert.IsInstanceOf<InvalidOperationException>(caller.ShutdownTask.Exception.InnerException, "It is refused");
            Assert.AreEqual("ShutdownAsync cannot wait for a startup it is called from; shut the host down from its coordinator.", caller.ShutdownTask.Exception.InnerException.Message, "The refusal names the coordinator");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The host still becomes ready");
            Assert.AreEqual(LifecycleState.Ready, region.GetLifecycleState(), "The scope is untouched");
        }

        /// <summary>
        /// After a shutdown the host accepts a new scope, and preparing again binds and initializes the scene against
        /// it.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Attach_AfterShutdown_PreparesSceneAgainAgainstNewScope()
        {
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver boot = BuildBoot();
            IObjectResolver region = CreateRegion(boot, null);
            host.Attach(region, ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);
            IRegionService svc1 = component.Service;
            Task shutdown = host.ShutdownAsync(CancellationToken.None);
            yield return WaitFor(shutdown);
            IObjectResolver region2 = CreateRegion(boot, null);

            host.Attach(region2, ScopeOwnership.Owned);
            Task prepare2 = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare2);

            Assert.AreNotSame(svc1, component.Service, "The component is bound to the new scope");
            Assert.AreEqual(2, component.InitializeCount, "The component is initialized once per scope");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The host is ready");
            Assert.AreSame(region2, host.Resolver, "The new scope is attached");
            Assert.AreEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The first region was shut down");
        }

        /// <summary>
        /// A region built from a boot-owned parent is shut down and its scene unloaded; the boot scope, its service,
        /// its scene and the object that owns it stay alive, and nothing is logged.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator ShutdownAsync_WhenRegionUsesBootOwnedParent_DisposesRegionAndKeepsParent()
        {
            Scene bootScene = SceneManager.CreateScene("Boot_" + Guid.NewGuid().ToString("N"));
            _scenes.Add(bootScene);
            GameObject bootOwner = new GameObject("BootOwner");
            _gameObjects.Add(bootOwner);
            SceneManager.MoveGameObjectToScene(bootOwner, bootScene);
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver boot = BuildBoot();
            IObjectResolver region = CreateRegion(boot, null);
            host.Attach(region, ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);
            IRegionService svc = component.Service;
            IBootService bootService = boot.Resolve<IBootService>();

            Task shutdown = host.ShutdownAsync(CancellationToken.None);
            yield return WaitFor(shutdown);
            yield return SceneManager.UnloadSceneAsync(_regionScene);

            Assert.AreSame(bootService, svc.Boot, "The region used the boot scope's service");
            Assert.AreEqual(1, svc.DisposeCount, "The region's service is disposed once");
            Assert.AreEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The region is shut down");
            Assert.AreEqual(LifecycleState.Ready, boot.GetLifecycleState(), "The boot scope stays ready");
            Assert.AreEqual(0, svc.Boot.DisposeCount, "The boot scope's service is not disposed");
            Assert.IsTrue(bootScene.isLoaded, "The boot scene stays loaded");
            Assert.IsTrue(bootOwner != null, "The boot scope's owner stays alive");
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>
        /// Unloading the region scene without a shutdown destroys the host, which shuts its owned resolver down and
        /// clears <c>Resolver</c>; the boot scope stays ready.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator OnDestroy_WhenRegionSceneUnloadedWithoutShutdown_ShutsOwnedResolverDown()
        {
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver boot = BuildBoot();
            IObjectResolver region = CreateRegion(boot, null);
            host.Attach(region, ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);
            IRegionService svc = component.Service;

            yield return SceneManager.UnloadSceneAsync(_regionScene);
            yield return null;

            Assert.AreEqual(1, svc.DisposeCount, "The region's service is disposed once");
            Assert.AreEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The owned resolver is shut down");
            Assert.IsNull(host.Resolver, "Resolver is cleared");
            Assert.AreEqual(LifecycleState.Ready, boot.GetLifecycleState(), "The boot scope stays ready");
        }

        /// <summary>
        /// Destroying a borrowed host leaves its resolver undisposed and detaches the host.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator OnDestroy_WhenBorrowedHostDestroyed_LeavesResolverUndisposed()
        {
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), null);
            host.Attach(region, ScopeOwnership.Borrowed);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);

            Object.Destroy(host.gameObject);
            yield return null;

            Assert.AreEqual(LifecycleState.Ready, region.GetLifecycleState(), "The borrowed resolver is left as it was");
            Assert.IsNull(host.Resolver, "Resolver is cleared");
            Assert.AreEqual(ScopeHostState.Detached, host.State, "The host is detached");
        }

        /// <summary>
        /// When an owned resolver's disposal throws during <c>OnDestroy</c>, each failure is logged as its own entry,
        /// in disposal order, and the resolver is still disposed.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator OnDestroy_WhenOwnedDisposalThrows_LogsEachFailure()
        {
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), RegisterThrowingDisposables);
            host.Attach(region, ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);
            region.Resolve<FirstThrowingDisposable>();
            region.Resolve<SecondThrowingDisposable>();
            LogAssert.Expect(LogType.Exception, new Regex("second dispose failed"));
            LogAssert.Expect(LogType.Exception, new Regex("first dispose failed"));

            Object.Destroy(host.gameObject);
            yield return null;

            LogAssert.NoUnexpectedReceived();
            Assert.AreEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The owned resolver is still disposed");
        }

        /// <summary>
        /// Destroying a host during a running prepare cancels the startup at once and shuts the owned resolver down
        /// only after the running participant returns.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator OnDestroy_WhenPrepareRunning_CancelsStartupAndShutsDownAfterParticipantReturns()
        {
            PlayModeGatedStartable g = new PlayModeGatedStartable();
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => g, Lifetime.Scoped));
            host.Attach(region, ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return null;

            Object.Destroy(host.gameObject);
            yield return null;
            yield return null;
            bool tokenCanceled = g.ReceivedToken.IsCancellationRequested;
            LifecycleState stateBeforeReturn = region.GetLifecycleState();
            g.Gate.SetResult(true);
            float deadline = Time.realtimeSinceStartup + 10f;
            while (region.GetLifecycleState() != LifecycleState.Disposed && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            yield return WaitFor(prepare);

            Assert.IsTrue(tokenCanceled, "The participant's token is cancelled at once");
            Assert.AreNotEqual(LifecycleState.Disposed, stateBeforeReturn, "Nothing is disposed while it runs");
            Assert.AreEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The owned resolver is shut down afterwards");
            Assert.IsTrue(prepare.IsCanceled, "The prepare is cancelled");
            Assert.IsNull(host.Resolver, "Resolver is cleared");
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>
        /// A shutdown called from inside a child host's startup, on the host that owns the parent, disposes the
        /// parent without waiting; the child host's startup then observes the disposal and faults.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator ShutdownAsync_WhenCalledFromInsideChildStartup_DisposesOwnedResolverWithoutWaiting()
        {
            Scene sessionScene = SceneManager.CreateScene("Session_" + Guid.NewGuid().ToString("N"));
            _scenes.Add(sessionScene);
            SceneManager.SetActiveScene(sessionScene);
            ExplicitScopeHost sessionHost = CreateHost();
            SceneManager.SetActiveScene(_regionScene);
            ExplicitScopeHost regionHost = CreateHost();
            IObjectResolver boot = BuildBoot();
            sessionHost.Attach(boot, ScopeOwnership.Owned);
            Task sessionPrepare = sessionHost.PrepareAsync(CancellationToken.None);
            yield return WaitFor(sessionPrepare);
            HostCallingStartable caller = new HostCallingStartable { Target = sessionHost, CallShutdown = true };
            IObjectResolver region = CreateRegion(boot, b => b.RegisterFactory(_ => caller, Lifetime.Scoped));
            regionHost.Attach(region, ScopeOwnership.Owned);

            Task prepare = regionHost.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);
            yield return WaitFor(caller.ShutdownTask);

            Assert.AreEqual(TaskStatus.RanToCompletion, caller.ShutdownTask.Status, "The parent host's shutdown completes");
            Assert.AreEqual(LifecycleState.Disposed, boot.GetLifecycleState(), "The parent is disposed");
            Assert.AreEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The child is disposed with it");
            Assert.AreEqual(ScopeHostState.Detached, sessionHost.State, "The parent host is detached");
            Assert.IsNull(sessionHost.Resolver, "The parent host's Resolver is cleared");
            Assert.AreEqual(ScopeHostState.Faulted, regionHost.State, "The child host's startup observed the disposal");
        }

        /// <summary>
        /// A borrowed scope that is already ready is supported: preparing binds and initializes the components, and
        /// the scope's startup does not run again.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenBorrowedScopeAlreadyReady_BindsAndInitializesComponents()
        {
            LoggedStartable s = new LoggedStartable();
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => s, Lifetime.Scoped));
            region.RunInitializables();
            Task startup = region.RunAsyncStartablesAsync(CancellationToken.None);
            yield return WaitFor(startup);
            host.Attach(region, ScopeOwnership.Borrowed);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);

            Assert.AreEqual(TaskStatus.RanToCompletion, prepare.Status, "The prepare completes");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The host is ready");
            Assert.IsNotNull(component.Service, "The component is bound");
            Assert.AreEqual(1, component.InitializeCount, "The component is initialized once");
            Assert.AreEqual(1, s.StartCount, "The scope's startup does not run again");
        }

        /// <summary>
        /// A resolver whose startup is running elsewhere is refused and the host stays detached.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Attach_WhenResolverStartupRunning_ThrowsInvalidOperationException()
        {
            PlayModeGatedStartable g = new PlayModeGatedStartable();
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => g, Lifetime.Scoped));
            region.RunInitializables();
            Task startup = region.RunAsyncStartablesAsync(CancellationToken.None);

            InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => host.Attach(region, ScopeOwnership.Borrowed), "A busy resolver is refused");
            ScopeHostState stateAfter = host.State;
            g.Gate.SetResult(true);
            yield return WaitFor(startup);

            StringAssert.Contains("running elsewhere", refusal.Message, "The refusal says the resolver is busy");
            Assert.AreEqual(ScopeHostState.Detached, stateAfter, "The host stays detached");
        }

        /// <summary>
        /// A shutdown started from a component's <c>Initialize</c>, inside the synchronous part of the prepare, cancels
        /// the prepare before startup, shuts the owned resolver down and detaches.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator ShutdownAsync_WhenCalledFromComponentInitialize_CancelsPrepareAndDetaches()
        {
            LoggedStartable s = new LoggedStartable();
            ExplicitScopeHost host = CreateHost();
            GameObject callerObject = new GameObject("ShutdownCaller");
            _gameObjects.Add(callerObject);
            ShutdownCallingComponent component = callerObject.AddComponent<ShutdownCallingComponent>();
            component.Target = host;
            IObjectResolver region = CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => s, Lifetime.Scoped));
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(component.ShutdownTask);
            yield return WaitFor(prepare);

            Assert.IsTrue(prepare.IsCanceled, "The prepare is cancelled");
            Assert.AreEqual(TaskStatus.RanToCompletion, component.ShutdownTask.Status, "The shutdown completes");
            Assert.AreEqual(ScopeHostState.Detached, host.State, "The host is detached");
            Assert.AreEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The owned resolver is shut down");
            Assert.AreEqual(0, s.StartCount, "Startup never ran");
        }

        /// <summary>
        /// A host destroyed from inside its own startup does not wait for that startup: it disposes the owned resolver
        /// at once.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator OnDestroy_WhenHostDestroyedInsideItsOwnStartup_DisposesOwnedResolverAtOnce()
        {
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            HostDestroyingStartable destroyer = new HostDestroyingStartable { Target = host.gameObject };
            IObjectResolver region = CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => destroyer, Lifetime.Scoped));
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return WaitFor(prepare);

            Assert.AreEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The owned resolver is disposed");
            Assert.IsTrue(prepare.IsCanceled, "The prepare is cancelled");
            Assert.IsNull(host.Resolver, "Resolver is cleared");
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>
        /// The parent scope shut down by its owner while a child host prepares stops the child's startup with a
        /// cancellation neither host token raised; the host faults and readiness is cancelled, never ready.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenParentScopeShutDownDuringStartup_FaultsHostAndCancelsReadiness()
        {
            PlayModeGatedStartable g = new PlayModeGatedStartable();
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver boot = BuildBoot();
            host.Attach(CreateRegion(boot, b => b.RegisterFactory(_ => g, Lifetime.Scoped)), ScopeOwnership.Borrowed);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return null;

            Task bootShutdown = boot.ShutdownAsync(CancellationToken.None);
            yield return null;
            g.Gate.SetResult(true);
            yield return WaitFor(prepare);
            Task ready = host.WaitUntilReadyAsync(CancellationToken.None);
            yield return WaitFor(ready);
            yield return WaitFor(bootShutdown);

            Assert.IsTrue(prepare.IsCanceled, "The prepare is cancelled");
            Assert.AreEqual(ScopeHostState.Faulted, host.State, "The host is faulted, never ready");
            Assert.IsTrue(ready.IsCanceled, "Readiness is cancelled");
            Assert.AreEqual(LifecycleState.Disposed, boot.GetLifecycleState(), "The parent is shut down");
        }

        /// <summary>
        /// Destroying a borrowed host during its startup cancels the scope's startup, which faults the scope, and
        /// leaves the scope undisposed; the host detaches once the startup settles.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator OnDestroy_WhenBorrowedHostDestroyedDuringStartup_CancelsStartupAndLeavesScopeUndisposed()
        {
            PlayModeGatedStartable g = new PlayModeGatedStartable();
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => g, Lifetime.Scoped));
            host.Attach(region, ScopeOwnership.Borrowed);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return null;

            Object.Destroy(host.gameObject);
            yield return null;
            bool tokenCanceled = g.ReceivedToken.IsCancellationRequested;
            g.Gate.SetResult(true);
            yield return WaitFor(prepare);
            float deadline = Time.realtimeSinceStartup + 10f;
            while (host.State != ScopeHostState.Detached && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.IsTrue(tokenCanceled, "The scope's startup is cancelled");
            Assert.IsTrue(prepare.IsCanceled, "The prepare is cancelled");
            Assert.AreEqual(LifecycleState.Faulted, region.GetLifecycleState(), "The scope is faulted, not disposed");
            Assert.AreEqual(ScopeHostState.Detached, host.State, "The host detaches");
        }

        /// <summary>
        /// A re-attach while the previous prepare is still settling: the stale prepare's continuation, arriving while
        /// the new attach is itself preparing, changes nothing on it, and leaves the new attach's cancellation source
        /// usable, so the new scope is still shut down afterwards.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Attach_WhenPreviousPrepareStillSettling_StalePrepareChangesNothing()
        {
            PlayModeGatedStartable g1 = new PlayModeGatedStartable();
            PlayModeGatedStartable g2 = new PlayModeGatedStartable();
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver boot = BuildBoot();
            IObjectResolver region1 = CreateRegion(boot, b => b.RegisterFactory(_ => g1, Lifetime.Scoped));
            host.Attach(region1, ScopeOwnership.Owned);
            Task prepare1 = host.PrepareAsync(CancellationToken.None);
            yield return null;
            using CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();
            Task shutdown = host.ShutdownAsync(cts.Token);
            yield return WaitFor(shutdown);
            IObjectResolver region2 = CreateRegion(boot, b => b.RegisterFactory(_ => g2, Lifetime.Scoped));
            host.Attach(region2, ScopeOwnership.Owned);
            Task prepare2 = host.PrepareAsync(CancellationToken.None);
            Task ready = host.WaitUntilReadyAsync(CancellationToken.None);
            yield return null;

            g1.Gate.SetResult(true);
            yield return null;
            yield return null;
            yield return WaitFor(prepare1);
            ScopeHostState stateAfterStalePrepare = host.State;
            bool readyIncompleteAfterStalePrepare = !ready.IsCompleted;
            g2.Gate.SetResult(true);
            yield return WaitFor(prepare2);
            yield return WaitFor(ready);
            ScopeHostState stateAfterSecondPrepare = host.State;
            IObjectResolver attachedAfterSecondPrepare = host.Resolver;
            Task shutdown2 = host.ShutdownAsync(CancellationToken.None);
            yield return WaitFor(shutdown2);

            Assert.IsTrue(shutdown.IsCanceled, "The shutdown stopped waiting at its cancelled token");
            Assert.AreEqual(LifecycleState.Disposed, region1.GetLifecycleState(), "The first region is disposed anyway");
            Assert.IsTrue(prepare1.IsCanceled, "The stale prepare ends cancelled");
            Assert.AreEqual(ScopeHostState.Preparing, stateAfterStalePrepare, "The stale prepare leaves the new attach preparing");
            Assert.IsTrue(readyIncompleteAfterStalePrepare, "The stale prepare leaves the new readiness pending");
            Assert.AreEqual(TaskStatus.RanToCompletion, ready.Status, "The new attach's readiness completes");
            Assert.AreEqual(ScopeHostState.Ready, stateAfterSecondPrepare, "The new attach becomes ready");
            Assert.AreSame(region2, attachedAfterSecondPrepare, "The new scope stays attached");
            Assert.AreEqual(2, component.InitializeCount, "The component was initialized once per scope");
            Assert.AreEqual(TaskStatus.RanToCompletion, shutdown2.Status, "The new attach shuts down cleanly");
            Assert.AreEqual(LifecycleState.Disposed, region2.GetLifecycleState(), "The new scope is shut down");
        }

        /// <summary>
        /// Null and destroyed roots in a given batch are skipped; the live roots are prepared.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenGivenRootsContainNullOrDestroyed_SkipsThem()
        {
            RegionComponent component = CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            host.Attach(CreateRegion(BuildBoot(), null), ScopeOwnership.Owned);
            GameObject gone = new GameObject("Gone");
            Object.Destroy(gone);
            yield return null;

            Task prepare = host.PrepareAsync(new GameObject[] { null, gone, component.gameObject }, CancellationToken.None);
            yield return WaitFor(prepare);

            Assert.AreEqual(TaskStatus.RanToCompletion, prepare.Status, "The prepare completes");
            Assert.IsNotNull(component.Service, "The live root is bound");
            Assert.AreEqual(1, component.InitializeCount, "The live root is initialized once");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The host is ready");
        }

        /// <summary>
        /// A shutdown called off Unity's main thread is refused through its task and changes nothing.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator ShutdownAsync_WhenCalledOffMainThread_FaultsWithInvalidOperationException()
        {
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), null);
            host.Attach(region, ScopeOwnership.Owned);

            Task shutdown = Task.Run(() => host.ShutdownAsync(CancellationToken.None));
            yield return WaitFor(shutdown);

            Assert.IsTrue(shutdown.IsFaulted, "The shutdown faults");
            Assert.IsInstanceOf<InvalidOperationException>(shutdown.Exception.InnerException, "It is refused");
            Assert.AreEqual("ExplicitScopeHost.ShutdownAsync must be called on Unity's main thread.", shutdown.Exception.InnerException.Message, "The refusal names the main thread");
            Assert.AreEqual(ScopeHostState.Attached, host.State, "The host stays attached");
            Assert.AreSame(region, host.Resolver, "The resolver stays attached");
            Assert.AreEqual(LifecycleState.Created, region.GetLifecycleState(), "The resolver is untouched");
        }

        /// <summary>
        /// A shutdown that joins one already running stops waiting when its own token is cancelled; the running
        /// shutdown continues and shuts the owned resolver down once the participant returns.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator ShutdownAsync_WhenJoiningAndTokenCanceled_IsCanceledWhileShutdownContinues()
        {
            PlayModeGatedStartable g = new PlayModeGatedStartable();
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            IObjectResolver region = CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => g, Lifetime.Scoped));
            host.Attach(region, ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return null;
            Task first = host.ShutdownAsync(CancellationToken.None);
            using CancellationTokenSource cts = new CancellationTokenSource();

            Task joining = host.ShutdownAsync(cts.Token);
            cts.Cancel();
            yield return WaitFor(joining);
            bool joiningCanceled = joining.IsCanceled;
            bool firstRunningThen = !first.IsCompleted;
            g.Gate.SetResult(true);
            yield return WaitFor(first);
            yield return WaitFor(prepare);

            Assert.IsTrue(joiningCanceled, "The joining call stops waiting at its own token");
            Assert.IsTrue(firstRunningThen, "The running shutdown keeps waiting for the participant");
            Assert.AreEqual(TaskStatus.RanToCompletion, first.Status, "The running shutdown completes");
            Assert.AreEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The owned resolver is shut down");
            Assert.AreEqual(ScopeHostState.Detached, host.State, "The host is detached");
        }

        /// <summary>
        /// A shutdown called from inside the host's own startup while a shutdown is already waiting for that startup
        /// is refused rather than joined, so a participant that awaits it cannot wait for itself.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator ShutdownAsync_WhenCalledFromInsideStartAsyncWhileStopping_FaultsWithInvalidOperationException()
        {
            CreateComponent("Component");
            ExplicitScopeHost host = CreateHost();
            GatedHostCallingStartable caller = new GatedHostCallingStartable { Target = host };
            IObjectResolver region = CreateRegion(BuildBoot(), b => b.RegisterFactory(_ => caller, Lifetime.Scoped));
            host.Attach(region, ScopeOwnership.Owned);
            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return null;
            Task shutdown = host.ShutdownAsync(CancellationToken.None);

            caller.Gate.SetResult(true);
            yield return WaitFor(shutdown);
            yield return WaitFor(prepare);
            yield return WaitFor(caller.ShutdownTask);

            Assert.IsTrue(caller.ShutdownTask.IsFaulted, "The call from inside the startup faults");
            Assert.IsInstanceOf<InvalidOperationException>(caller.ShutdownTask.Exception.InnerException, "It is refused");
            Assert.AreEqual("ShutdownAsync cannot wait for a startup it is called from; shut the host down from its coordinator.", caller.ShutdownTask.Exception.InnerException.Message, "The refusal names the coordinator");
            Assert.AreEqual(TaskStatus.RanToCompletion, shutdown.Status, "The running shutdown completes");
            Assert.AreEqual(LifecycleState.Disposed, region.GetLifecycleState(), "The owned resolver is shut down");
        }

        /// <summary>
        /// Yields frames until <paramref name="task"/> completes or 10 s of real time pass; never blocks on it.
        /// </summary>
        /// <param name="task">The task to wait for.</param>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        private IEnumerator WaitFor(Task task)
        {
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
        }

        /// <summary>
        /// Creates a tracked GameObject named <c>Host</c> in the active scene and adds an
        /// <see cref="ExplicitScopeHost"/> to it.
        /// </summary>
        /// <returns>The new host.</returns>
        private ExplicitScopeHost CreateHost()
        {
            GameObject hostObject = new GameObject("Host");
            _gameObjects.Add(hostObject);
            return hostObject.AddComponent<ExplicitScopeHost>();
        }

        /// <summary>
        /// Creates a tracked, active GameObject in the active scene with a <see cref="RegionComponent"/>, whose
        /// <c>Awake</c> runs at once.
        /// </summary>
        /// <param name="name">The GameObject's name.</param>
        /// <returns>The new component.</returns>
        private RegionComponent CreateComponent(string name)
        {
            GameObject componentObject = new GameObject(name);
            _gameObjects.Add(componentObject);
            return componentObject.AddComponent<RegionComponent>();
        }

        /// <summary>
        /// Builds a tracked boot root that registers <see cref="IBootService"/> as a Singleton, runs its pass and its
        /// startup, which completes at once since it has no startables, so a region built from it can start.
        /// </summary>
        /// <returns>The ready boot root.</returns>
        private IObjectResolver BuildBoot()
        {
            IObjectResolver boot = Container.Build(b => b.Register<IBootService, BootService>(Lifetime.Singleton));
            _resolvers.Add(boot);
            boot.RunInitializables();
            _ = boot.RunAsyncStartablesAsync(CancellationToken.None);
            return boot;
        }

        /// <summary>
        /// Creates a tracked region scope from <paramref name="boot"/> that registers <see cref="IRegionService"/> as
        /// Scoped, plus whatever <paramref name="extra"/> registers.
        /// </summary>
        /// <param name="boot">The parent scope.</param>
        /// <param name="extra">Further registrations; null for none.</param>
        /// <returns>The new region scope, not yet initialized.</returns>
        private IObjectResolver CreateRegion(IObjectResolver boot, Action<IContainerBuilder> extra)
        {
            IObjectResolver region = boot.CreateChildScope(b =>
            {
                b.Register<IRegionService, RegionService>(Lifetime.Scoped);
                extra?.Invoke(b);
            });
            _resolvers.Add(region);
            return region;
        }

        /// <summary>
        /// Registers <see cref="FirstThrowingDisposable"/> and <see cref="SecondThrowingDisposable"/> as Scoped, so
        /// disposing the region meets two cleanup failures.
        /// </summary>
        /// <param name="builder">The region's builder.</param>
        private void RegisterThrowingDisposables(IContainerBuilder builder)
        {
            builder.RegisterFactory(_ => new FirstThrowingDisposable(), Lifetime.Scoped);
            builder.RegisterFactory(_ => new SecondThrowingDisposable(), Lifetime.Scoped);
        }
    }
}
