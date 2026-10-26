using System.Collections;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
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
    /// Pins the one-owner rules:
    /// <list type="bullet">
    ///     <item>A host's batch holds no other host outside a <see cref="ScopeBoundary"/>, active or not, enabled or not; an automatic host refuses from <c>Awake</c> with <see cref="ScopeOwnershipException"/> and binds nothing</item>
    ///     <item>An explicit host refuses in <c>PrepareAsync</c> with <see cref="ScopeOwnershipException"/>, changes nothing and stays attached, so a fixed scene then prepares</item>
    ///     <item>Given roots do not overlap: the same root twice, or one beneath another, is refused</item>
    ///     <item>Every violation is reported in one exception naming each object as <c>{scene}/{path}</c></item>
    ///     <item>An automatic host inside a boundary refuses; an explicit host inside one belongs to that subtree's preparer</item>
    ///     <item><c>InjectGameObject</c> checks boundaries but never hosts; given roots may span scenes</item>
    /// </list>
    /// Every scene and GameObject is runtime-created; tasks are driven by yielding frames until they complete or 10 s
    /// of real time pass. An exception thrown from an <c>Awake</c> that <c>AddComponent</c> runs is logged by Unity, not
    /// thrown to the caller.
    /// </summary>
    /// <remarks>
    /// Exercises <see cref="LifetimeScopeBehaviour"/>, <see cref="ExplicitScopeHost"/>,
    /// <see cref="UnityResolverExtensions.InjectGameObject"/> and <see cref="ScopeOwnershipException"/>.
    /// </remarks>
    [TestFixture]
    public sealed class ScopeOwnershipTests
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
            yield return _bed.SetUp("Region");
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
        /// A second automatic host added to a prepared scene refuses alone: its <c>Awake</c> throws
        /// <see cref="ScopeOwnershipException"/>, which Unity logs, and the first host's component stays bound to the
        /// first scope and initialized once.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenSecondAutomaticHostAdded_LogsScopeOwnershipExceptionAndFirstHostInitializesOnce()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("Component", null);
            TestRootScope hostA = _bed.Add<TestRootScope>("HostA", null);
            IPlayModeService first = hostA.Resolver.Resolve<IPlayModeService>();
            LogAssert.Expect(LogType.Exception, new Regex("ScopeOwnershipException"));

            _bed.Add<TestRootScope>("HostB", null);

            Assert.AreEqual(1, c.InitializeCount, "The component is initialized once, by the first host");
            Assert.AreSame(first, c.Service, "The component stays bound to the first scope");
            LogAssert.NoUnexpectedReceived();
            yield return null;
        }

        /// <summary>
        /// An automatic host whose scene holds an explicit host refuses: its <c>Awake</c> throws
        /// <see cref="ScopeOwnershipException"/>, which Unity logs.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenSceneHasExplicitHost_LogsScopeOwnershipException()
        {
            _bed.Add<ExplicitScopeHost>("Explicit", null);
            LogAssert.Expect(LogType.Exception, new Regex("ScopeOwnershipException"));

            _bed.Add<TestRootScope>("Host", null);

            LogAssert.NoUnexpectedReceived();
            yield return null;
        }

        /// <summary>
        /// An explicit host whose scene holds an automatic host refuses its prepare with
        /// <see cref="ScopeOwnershipException"/> naming itself first, then the other host; it stays attached and runs
        /// no pass.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenSceneHasAutomaticHost_RefusesWithScopeOwnershipExceptionAndStaysAttached()
        {
            _bed.Add<TestRootScope>("Auto", null);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Explicit", null);
            IObjectResolver region = CreateRegion();
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<ScopeOwnershipException>(prepare.Exception.InnerException, "It is refused for ownership");
            string scene = _bed.Scene.name;
            StringAssert.StartsWith($"More than one scope host owns these objects: {scene}/Explicit; {scene}/Auto.", prepare.Exception.InnerException.Message, "The message names the preparing host first, then the other");
            Assert.AreEqual(ScopeHostState.Attached, host.State, "The host stays attached");
            Assert.AreEqual(LifecycleState.Created, region.GetLifecycleState(), "No pass ran");
        }

        /// <summary>
        /// An explicit host whose scene holds a second explicit host refuses its prepare, naming both, binds and
        /// initializes nothing, and returns every discovery rental of the refused prepare.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenSceneHasSecondExplicitHost_RefusesAndBindsNothing()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("Component", null);
            ExplicitScopeHost host1 = _bed.Add<ExplicitScopeHost>("Host1", null);
            _bed.Add<ExplicitScopeHost>("Host2", null);
            IObjectResolver region = CreateRegion();
            host1.Attach(region, ScopeOwnership.Owned);
            int rentalsBefore = ComponentDiscovery.CountOutstandingRentals();

            Task prepare = host1.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<ScopeOwnershipException>(prepare.Exception.InnerException, "It is refused for ownership");
            string scene = _bed.Scene.name;
            StringAssert.Contains($"{scene}/Host1; {scene}/Host2", prepare.Exception.InnerException.Message, "The message names both hosts");
            Assert.IsNull(c.Service, "Nothing is bound");
            Assert.AreEqual(0, c.InitializeCount, "Nothing is initialized");
            Assert.AreEqual(ScopeHostState.Attached, host1.State, "The host stays attached");
            Assert.AreEqual(LifecycleState.Created, region.GetLifecycleState(), "No pass ran");
            Assert.AreEqual(rentalsBefore, ComponentDiscovery.CountOutstandingRentals(), "The refusal returned its discovery targets");
        }

        /// <summary>
        /// A second explicit host inside a boundary subtree belongs to that subtree's preparer, so the scene's host
        /// prepares the rest of the scene and leaves the subtree unbound.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenSecondExplicitHostInsideScopeBoundary_PreparesScene()
        {
            BoundCountingComponent outside = _bed.Add<BoundCountingComponent>("Outside", null);
            ExplicitScopeHost host1 = _bed.Add<ExplicitScopeHost>("Host1", null);
            GameObject boundary = _bed.Create("Boundary", null);
            boundary.AddComponent<ScopeBoundary>();
            _bed.Add<ExplicitScopeHost>("Host2", boundary);
            BoundCountingComponent inside = _bed.Add<BoundCountingComponent>("Inside", boundary);
            host1.Attach(CreateRegion(), ScopeOwnership.Owned);

            Task prepare = host1.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.AreEqual(TaskStatus.RanToCompletion, prepare.Status, "The prepare ran to completion");
            Assert.AreEqual(ScopeHostState.Ready, host1.State, "The host is ready");
            Assert.IsNotNull(outside.Service, "The outside component is bound");
            Assert.IsNull(inside.Service, "The boundary subtree is left unbound");
        }

        /// <summary>
        /// Given roots where one lies beneath another are refused, naming the inner root and its given ancestor;
        /// nothing is bound, the host stays attached, and every discovery rental of the refused prepare is returned.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenGivenRootsOverlap_RefusesAndBindsNothing()
        {
            GameObject p = _bed.Create("P", null);
            BoundCountingComponent cp = p.AddComponent<BoundCountingComponent>();
            GameObject c = _bed.Create("C", p);
            BoundCountingComponent cc = c.AddComponent<BoundCountingComponent>();
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            host.Attach(CreateRegion(), ScopeOwnership.Owned);
            int rentalsBefore = ComponentDiscovery.CountOutstandingRentals();

            Task prepare = host.PrepareAsync(new[] { p, c }, CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<ScopeOwnershipException>(prepare.Exception.InnerException, "It is refused for ownership");
            string scene = _bed.Scene.name;
            StringAssert.Contains($"The given roots overlap: {scene}/P/C is beneath {scene}/P. Give each root once.", prepare.Exception.InnerException.Message, "The message names the inner root and its ancestor");
            Assert.IsNull(cp.Service, "The outer root is not bound");
            Assert.IsNull(cc.Service, "The inner root is not bound");
            Assert.AreEqual(ScopeHostState.Attached, host.State, "The host stays attached");
            Assert.AreEqual(rentalsBefore, ComponentDiscovery.CountOutstandingRentals(), "The refusal returned its discovery targets and sets");
        }

        /// <summary>
        /// The same root given twice is refused, naming it once.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenSameRootGivenTwice_Refuses()
        {
            GameObject a = _bed.Create("A", null);
            a.AddComponent<BoundCountingComponent>();
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            host.Attach(CreateRegion(), ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(new[] { a, a }, CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<ScopeOwnershipException>(prepare.Exception.InnerException, "It is refused for ownership");
            StringAssert.Contains($"The given roots overlap: {_bed.Scene.name}/A is given twice. Give each root once.", prepare.Exception.InnerException.Message, "The message names the root given twice");
            Assert.AreEqual(ScopeHostState.Attached, host.State, "The host stays attached");
        }

        /// <summary>
        /// A batch with both a host conflict and overlapping given roots is refused with one
        /// <see cref="ScopeOwnershipException"/> that reports each.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenSeveralConflicts_ReportsEachInOneException()
        {
            GameObject p = _bed.Create("P", null);
            _bed.Add<ExplicitScopeHost>("Host2", p);
            GameObject c = _bed.Create("C", p);
            ExplicitScopeHost host1 = _bed.Add<ExplicitScopeHost>("Host1", null);
            host1.Attach(CreateRegion(), ScopeOwnership.Owned);

            Task prepare = host1.PrepareAsync(new[] { p, c }, CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<ScopeOwnershipException>(prepare.Exception.InnerException, "One ownership exception");
            string message = prepare.Exception.InnerException.Message;
            StringAssert.Contains("More than one scope host owns these objects:", message, "The host conflict is reported");
            StringAssert.Contains("The given roots overlap:", message, "The overlap is reported");
        }

        /// <summary>
        /// <c>InjectGameObject</c> over a tree that holds a host binds it without a host check.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenTreeContainsHost_BindsWithoutRefusing()
        {
            GameObject root = _bed.Create("Root", null);
            InjectableMonoBehaviour i = root.AddComponent<InjectableMonoBehaviour>();
            _bed.Add<ExplicitScopeHost>("Host", root);
            IObjectResolver region = CreateRegion();

            region.InjectGameObject(root);

            Assert.IsNotNull(i.Service, "The tree is bound and nothing is refused");
            yield return null;
        }

        /// <summary>
        /// An automatic host inside a boundary refuses from <c>Awake</c>: Unity logs the
        /// <see cref="ScopeOwnershipException"/>, and nothing in the scene is bound or initialized.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenHostInsideScopeBoundary_LogsScopeOwnershipExceptionAndBindsNothing()
        {
            BoundCountingComponent outside = _bed.Add<BoundCountingComponent>("Outside", null);
            GameObject boundary = _bed.Create("Boundary", null);
            boundary.AddComponent<ScopeBoundary>();
            LogAssert.Expect(LogType.Exception, new Regex("A LifetimeScopeBehaviour cannot sit inside a ScopeBoundary"));

            _bed.Add<TestRootScope>("Host", boundary);

            Assert.IsNull(outside.Service, "Nothing is bound");
            Assert.AreEqual(0, outside.InitializeCount, "Nothing is initialized");
            LogAssert.NoUnexpectedReceived();
            yield return null;
        }

        /// <summary>
        /// A refused prepare changes nothing, so once the conflicting host is gone the next prepare succeeds and
        /// binds and initializes the scene once.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenConflictRemovedAfterRefusal_PreparesScene()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("Component", null);
            ExplicitScopeHost host1 = _bed.Add<ExplicitScopeHost>("Host1", null);
            ExplicitScopeHost host2 = _bed.Add<ExplicitScopeHost>("Host2", null);
            host1.Attach(CreateRegion(), ScopeOwnership.Owned);
            Task first = host1.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(first);
            Object.Destroy(host2.gameObject);
            yield return null;

            Task second = host1.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(second);

            Assert.IsTrue(first.IsFaulted, "The first prepare faults");
            Assert.IsInstanceOf<ScopeOwnershipException>(first.Exception.InnerException, "The first prepare is refused for ownership");
            Assert.AreEqual(TaskStatus.RanToCompletion, second.Status, "The second prepare ran to completion");
            Assert.AreEqual(ScopeHostState.Ready, host1.State, "The host is ready");
            Assert.IsNotNull(c.Service, "The component is bound");
            Assert.AreEqual(1, c.InitializeCount, "The component is initialized once");
        }

        /// <summary>
        /// A second host on an inactive GameObject and a third whose component is disabled still count, so the
        /// prepare is refused naming both.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenSecondHostInactiveOrDisabled_Refuses()
        {
            ExplicitScopeHost host1 = _bed.Add<ExplicitScopeHost>("Host1", null);
            ExplicitScopeHost host2 = _bed.Add<ExplicitScopeHost>("Host2", null);
            host2.gameObject.SetActive(false);
            ExplicitScopeHost host3 = _bed.Add<ExplicitScopeHost>("Host3", null);
            host3.enabled = false;
            host1.Attach(CreateRegion(), ScopeOwnership.Owned);

            Task prepare = host1.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<ScopeOwnershipException>(prepare.Exception.InnerException, "It is refused for ownership");
            string scene = _bed.Scene.name;
            StringAssert.Contains($"{scene}/Host2", prepare.Exception.InnerException.Message, "The host on an inactive object counts");
            StringAssert.Contains($"{scene}/Host3", prepare.Exception.InnerException.Message, "The disabled host counts");
        }

        /// <summary>
        /// Given roots in two scenes are prepared together: neither the overlap nor the host check is confined to the
        /// host's own scene.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenGivenRootsSpanTwoScenes_PreparesBoth()
        {
            Scene session = _bed.CreateExtraScene("Session");
            GameObject rootA = _bed.Create("A", null);
            BoundCountingComponent a = rootA.AddComponent<BoundCountingComponent>();
            GameObject rootB = _bed.Create("B", null);
            BoundCountingComponent b = rootB.AddComponent<BoundCountingComponent>();
            SceneManager.MoveGameObjectToScene(rootB, session);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            host.Attach(CreateRegion(), ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(new[] { rootA, rootB }, CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.AreEqual(TaskStatus.RanToCompletion, prepare.Status, "The prepare ran to completion");
            Assert.IsNotNull(a.Service, "The root in the host's scene is bound");
            Assert.AreEqual(1, a.InitializeCount, "The root in the host's scene is initialized once");
            Assert.IsNotNull(b.Service, "The root in the other scene is bound");
            Assert.AreEqual(1, b.InitializeCount, "The root in the other scene is initialized once");
        }

        /// <summary>
        /// Builds a tracked region resolver that registers <see cref="IPlayModeService"/> as a Singleton.
        /// </summary>
        /// <returns>The new region resolver, not yet initialized.</returns>
        private IObjectResolver CreateRegion()
        {
            return _bed.Track(Container.Build(b => b.Register<IPlayModeService, PlayModeService>(Lifetime.Singleton)));
        }
    }
}
