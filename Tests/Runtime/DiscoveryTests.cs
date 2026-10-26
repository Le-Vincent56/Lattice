using System;
using System.Collections;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Unity;
using Didionysymus.Lattice.Tests.Runtime.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Didionysymus.Lattice.Tests.Runtime
{
    /// <summary>
    /// Pins how a batch's components are discovered:
    /// <list type="bullet">
    ///     <item>One walk: a host initializes only what its walk found before the service pass, so a component added beneath a root during the pass is in neither role</item>
    ///     <item>Eligibility: a component with no <c>[Inject]</c> member, inherited ones included, is never passed to <c>Inject</c>; one with inherited members is bound before <c>Start</c>; an initializable without members is in the initialize role only</item>
    ///     <item>Skips: null (missing-script) and destroyed entries are skipped at collection and at use; inactive descendants are in both roles</item>
    ///     <item>Buffers: a nested walk gets its own buffer, and every rental is returned cleared, even when the bind throws</item>
    ///     <item>Extensions: <c>InjectGameObject</c> refuses a caller off Unity's main thread before touching anything</item>
    /// </list>
    /// Every scene and GameObject is runtime-created; tasks are driven by yielding frames until they complete or 10 s
    /// of real time pass.
    /// </summary>
    /// <remarks>
    /// Exercises <see cref="LifetimeScopeBehaviour"/>, <see cref="ExplicitScopeHost"/>,
    /// <see cref="UnityResolverExtensions"/> and, through the adapter's <c>InternalsVisibleTo</c> grant to this
    /// assembly, the internal <see cref="ComponentDiscovery"/> and <see cref="InjectionTargets"/>.
    /// </remarks>
    [TestFixture]
    public sealed class DiscoveryTests
    {
        private SceneTestBed _bed;

        /// <summary>
        /// Resets the fixtures' statics and creates a fresh bed with its own active scene.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            ComponentAddingInitializable.Target = null;
            ComponentAddingInitializable.Added = null;
            ComponentDestroyingInitializable.Target = null;
            _bed = new SceneTestBed();
            yield return _bed.SetUp("Discovery");
        }

        /// <summary>
        /// Tears the bed down.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            yield return _bed.TearDown();
        }

        /// <summary>
        /// A component a service's <c>Initialize</c> adds beneath a scene root during the automatic host's pass was
        /// not in the walk, so the host neither binds nor initializes it.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenComponentAddedBeneathRootDuringServicePass_DoesNotInitializeIt()
        {
            GameObject root = _bed.Create("Root", null);
            ComponentAddingInitializable.Target = root;

            _bed.Add<ComponentAddingHost>("Host", null);

            Assert.IsNotNull(ComponentAddingInitializable.Added, "The service pass added the component");
            Assert.AreEqual(0, ComponentAddingInitializable.Added.InitializeCount, "A component added during the pass is not initialized");
            Assert.IsNull(ComponentAddingInitializable.Added.Service, "A component added during the pass is not bound");
            yield return null;
        }

        /// <summary>
        /// A component a service's <c>Initialize</c> adds beneath a scene root during the explicit host's pass was
        /// not in the walk, so the host does not initialize it and still becomes ready.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenComponentAddedBeneathRootDuringServicePass_DoesNotInitializeIt()
        {
            GameObject root = _bed.Create("Root", null);
            ComponentAddingInitializable.Target = root;
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            IObjectResolver region = _bed.Track(Container.Build(b =>
            {
                b.Register<IPlayModeService, PlayModeService>(Lifetime.Singleton);
                b.Register<ComponentAddingInitializable>(Lifetime.Singleton);
            }));
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.AreEqual(TaskStatus.RanToCompletion, prepare.Status, "The prepare ran to completion");
            Assert.IsNotNull(ComponentAddingInitializable.Added, "The service pass added the component");
            Assert.AreEqual(0, ComponentAddingInitializable.Added.InitializeCount, "A component added during the pass is not initialized");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The host is ready");
        }

        /// <summary>
        /// A component with no <c>[Inject]</c> members is never passed to <c>Inject</c>; one with members is.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenComponentHasNoInjectMembers_DoesNotCallInject()
        {
            GameObject root = _bed.Create("Root", null);
            root.AddComponent<PlainMonoBehaviour>();
            InjectableMonoBehaviour i = root.AddComponent<InjectableMonoBehaviour>();
            CountingResolver r = new CountingResolver();

            r.InjectGameObject(root);

            CollectionAssert.AreEqual(new object[] { i }, r.Injected, "Only the component with an [Inject] member is passed to Inject");
            yield return null;
        }

        /// <summary>
        /// Every component with <c>[Inject]</c> members beneath the root, on the root and on a child, is passed to
        /// <c>Inject</c> exactly once.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenComponentHasInjectMembers_CallsInjectOncePerComponent()
        {
            GameObject root = _bed.Create("Root", null);
            InjectableMonoBehaviour a = root.AddComponent<InjectableMonoBehaviour>();
            InjectableMonoBehaviour b = _bed.Add<InjectableMonoBehaviour>("Child", root);
            CountingResolver r = new CountingResolver();

            r.InjectGameObject(root);

            CollectionAssert.AreEquivalent(new object[] { a, b }, r.Injected, "Each injectable component is passed to Inject once");
            yield return null;
        }

        /// <summary>
        /// A disposed scope is never asked to bind a hierarchy whose components have no <c>[Inject]</c> members, so
        /// <c>InjectGameObject</c> does not throw.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenScopeDisposedAndNoComponentHasInjectMembers_DoesNotThrow()
        {
            GameObject root = _bed.Create("Root", null);
            root.AddComponent<PlainMonoBehaviour>();
            root.AddComponent<CountingInitializableMonoBehaviour>();
            IObjectResolver region = _bed.Track(Container.Build(b => b.Register<IPlayModeService, PlayModeService>(Lifetime.Singleton)));
            region.Dispose();

            Assert.DoesNotThrow(() => region.InjectGameObject(root), "Nothing is passed to the disposed scope");
            yield return null;
        }

        /// <summary>
        /// A component whose <c>[Inject]</c> field, property and method are all declared on an abstract base is bound
        /// by the automatic host before its <c>Start</c>.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenComponentInheritsInjectMembers_PopulatesThemBeforeStart()
        {
            DerivedInjectedComponent d = _bed.Add<DerivedInjectedComponent>("Derived", null);

            TestRootScope host = _bed.Add<TestRootScope>("Host", null);
            yield return null;

            Assert.IsTrue(d.AllBoundAtStart, "Every inherited member was bound before Start");
            IPlayModeService service = host.Resolver.Resolve<IPlayModeService>();
            Assert.AreSame(service, d.FieldService, "The inherited private field was bound");
            Assert.AreSame(service, d.InjectedProperty, "The inherited private property was bound");
            Assert.AreSame(service, d.MethodService, "The inherited private [Inject] method ran");
        }

        /// <summary>
        /// A component a service destroys with <c>DestroyImmediate</c> during the automatic host's pass is skipped at
        /// use, so it is not initialized.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenComponentDestroyedImmediatelyDuringServicePass_DoesNotInitializeIt()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("Component", null);
            ComponentDestroyingInitializable.Target = c;

            _bed.Add<ComponentDestroyingHost>("Host", null);

            Assert.IsFalse(c, "The service pass destroyed the component");
            Assert.AreEqual(0, c.InitializeCount, "A destroyed component is not initialized");
            yield return null;
        }

        /// <summary>
        /// A component whose <c>Initialize</c> calls <c>InitializeGameObject</c> on another root runs a nested walk
        /// with its own buffer, so both roots are initialized once and the outer walk continues.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InitializeGameObject_WhenCalledFromInsideComponentInitialize_InitializesBothRoots()
        {
            GameObject rootA = _bed.Create("RootA", null);
            NestedInitializingComponent n = rootA.AddComponent<NestedInitializingComponent>();
            CountingInitializableMonoBehaviour ca = rootA.AddComponent<CountingInitializableMonoBehaviour>();
            GameObject rootB = _bed.Create("RootB", null);
            CountingInitializableMonoBehaviour cb = rootB.AddComponent<CountingInitializableMonoBehaviour>();
            IObjectResolver region = _bed.Track(Container.Build(b => b.Register<IPlayModeService, PlayModeService>(Lifetime.Singleton)));
            region.RunInitializables();
            n.Resolver = region;
            n.OtherRoot = rootB;

            region.InitializeGameObject(rootA);

            Assert.AreEqual(1, n.InitializeCount, "The nesting component is initialized once");
            Assert.AreEqual(1, ca.InitializeCount, "The outer walk continues after the nested one");
            Assert.AreEqual(1, cb.InitializeCount, "The nested walk initializes the other root");
            yield return null;
        }

        /// <summary>
        /// The classify step skips a null (missing-script) entry and a destroyed component in both roles.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Collect_WhenBufferHasNullAndDestroyedEntries_SkipsThem()
        {
            InjectableInitializableMonoBehaviour live = _bed.Add<InjectableInitializableMonoBehaviour>("Live", null);
            InjectableInitializableMonoBehaviour gone = _bed.Add<InjectableInitializableMonoBehaviour>("Gone", null);
            Object.DestroyImmediate(gone);
            InjectionTargets t = ComponentDiscovery.Rent();

            try
            {
                ComponentDiscovery.AddRoles(new MonoBehaviour[] { null, gone, live }, t);

                CollectionAssert.AreEqual(new[] { live }, t.Inject, "Only the live component is an inject target");
                CollectionAssert.AreEqual(new[] { live }, t.Initialize, "Only the live component is an initialize target");
            }
            finally
            {
                ComponentDiscovery.Return(t);
            }

            yield return null;
        }

        /// <summary>
        /// Returned targets are cleared before they go back to the pool, so no pooled collection keeps a scene
        /// reference.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Collect_WhenTargetsReturned_LeavesPooledBuffersEmpty()
        {
            GameObject root = _bed.Create("Root", null);
            root.AddComponent<InjectableInitializableMonoBehaviour>();
            InjectionTargets t = ComponentDiscovery.Rent();

            try
            {
                ComponentDiscovery.CollectRoot(root, t);
                Assert.AreEqual(1, t.Inject.Count, "Pre-condition: the walk found the component");
            }
            finally
            {
                ComponentDiscovery.Return(t);
            }

            Assert.AreEqual(0, ComponentDiscovery.CountPooledReferences(), "Every pooled collection is empty");
            yield return null;
        }

        /// <summary>
        /// A component on an inactive descendant is bound and initialized by the automatic host and left inactive.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenInactiveDescendantHasInjectMembers_BindsAndInitializesIt()
        {
            GameObject parent = _bed.Create("Parent", null);
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("Child", parent);
            c.gameObject.SetActive(false);

            _bed.Add<TestRootScope>("Host", null);

            Assert.IsNotNull(c.Service, "The inactive component is bound");
            Assert.AreEqual(1, c.InitializeCount, "The inactive component is initialized once");
            Assert.IsFalse(c.gameObject.activeSelf, "The host activates nothing");
            yield return null;
        }

        /// <summary>
        /// The classify step puts an initializable without <c>[Inject]</c> members in the initialize role only, an
        /// injectable non-initializable in the inject role only, and a plain component in neither.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Collect_WhenComponentIsInitializableWithoutInjectMembers_ListsItForInitializeOnly()
        {
            CountingInitializableMonoBehaviour c = _bed.Add<CountingInitializableMonoBehaviour>("Initializable", null);
            InjectableMonoBehaviour i = _bed.Add<InjectableMonoBehaviour>("Injectable", null);
            PlainMonoBehaviour p = _bed.Add<PlainMonoBehaviour>("Plain", null);
            InjectionTargets t = ComponentDiscovery.Rent();

            try
            {
                ComponentDiscovery.AddRoles(new MonoBehaviour[] { c, i, p }, t);

                CollectionAssert.AreEqual(new[] { i }, t.Inject, "Only the injectable component is an inject target");
                CollectionAssert.AreEqual(new[] { c }, t.Initialize, "Only the initializable component is an initialize target");
            }
            finally
            {
                ComponentDiscovery.Return(t);
            }

            yield return null;
        }

        /// <summary>
        /// <c>InjectGameObject</c> called off Unity's main thread throws <see cref="InvalidOperationException"/>
        /// naming the member, before any component is passed to the resolver.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenCalledOffMainThread_ThrowsInvalidOperationException()
        {
            GameObject root = _bed.Create("Root", null);
            root.AddComponent<InjectableMonoBehaviour>();
            CountingResolver r = new CountingResolver();

            Task call = Task.Run(() => r.InjectGameObject(root));
            yield return SceneTestBed.WaitFor(call);

            Assert.IsTrue(call.IsFaulted, "The call faults");
            Assert.IsInstanceOf<InvalidOperationException>(call.Exception.InnerException, "It is refused");
            Assert.AreEqual("UnityResolverExtensions.InjectGameObject must be called on Unity's main thread.", call.Exception.InnerException.Message, "The refusal names the member and the main thread");
            CollectionAssert.IsEmpty(r.Injected, "Nothing was passed to the resolver");
        }

        /// <summary>
        /// When the automatic host's bind throws, every target and buffer it rented is still returned, so nothing is
        /// outstanding and no pooled collection keeps a scene reference.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenBindThrows_ReturnsEveryRental()
        {
            _bed.Add<MissingDependencyComponent>("Component", null);
            LogAssert.Expect(LogType.Exception, new Regex("RegistrationNotFoundException"));

            _bed.Add<TestRootScope>("Host", null);

            Assert.AreEqual(0, ComponentDiscovery.CountOutstandingRentals(), "Every rental was returned");
            Assert.AreEqual(0, ComponentDiscovery.CountPooledReferences(), "Every pooled collection is empty");
            LogAssert.NoUnexpectedReceived();
            yield return null;
        }

        /// <summary>
        /// A component whose <c>[Inject]</c> method calls <c>InjectGameObject</c> on another root runs a nested bind
        /// with its own buffer, so both roots are bound and the outer walk continues.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenInjectMethodInjectsAnotherRoot_BindsBoth()
        {
            GameObject rootA = _bed.Create("RootA", null);
            NestedInjectingComponent n = rootA.AddComponent<NestedInjectingComponent>();
            InjectableMonoBehaviour a = rootA.AddComponent<InjectableMonoBehaviour>();
            GameObject rootB = _bed.Create("RootB", null);
            InjectableMonoBehaviour b = rootB.AddComponent<InjectableMonoBehaviour>();
            IObjectResolver region = _bed.Track(Container.Build(builder => builder.Register<IPlayModeService, PlayModeService>(Lifetime.Singleton)));
            n.Resolver = region;
            n.OtherRoot = rootB;

            region.InjectGameObject(rootA);

            Assert.IsNotNull(n.Service, "The nesting component is bound");
            Assert.IsNotNull(a.Service, "The outer walk continues after the nested bind");
            Assert.IsNotNull(b.Service, "The nested bind binds the other root");
            yield return null;
        }
    }
}
