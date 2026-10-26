using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Unity;
using Didionysymus.Lattice.Tests.Runtime.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Didionysymus.Lattice.Tests.Runtime
{
    /// <summary>
    /// Pins how a valid manifest is used:
    /// <list type="bullet">
    ///     <item>A host's scene batch reads the scene manifest on the host's own GameObject and prepares exactly its lists without walking; a valid empty manifest prepares nothing and walks nothing</item>
    ///     <item>A component added after generation is not prepared, although a walk would have found it</item>
    ///     <item>A given-roots batch reads each root's hierarchy manifest and walks only the roots without one</item>
    ///     <item><c>InjectGameObject</c> and <c>InitializeGameObject</c> read the root's hierarchy manifest; a manifest of the other coverage is ignored and the unit walked</item>
    ///     <item>A host without a manifest walks each scene root once, as before</item>
    ///     <item><c>InstantiateAndInject</c> reads the clone's own manifest, whose references point at the clone, so neither the source nor another clone is touched</item>
    ///     <item>A disabled manifest, or one on an inactive root, still counts</item>
    /// </list>
    /// "No walk" is a before/after delta of <see cref="ComponentDiscovery.CountWalks"/> read just before the act. Every
    /// scene and GameObject is runtime-created; tasks are driven by yielding frames until they complete or 10 s of real
    /// time pass.
    /// </summary>
    /// <remarks>
    /// Exercises <see cref="LifetimeScopeBehaviour"/>, <see cref="ExplicitScopeHost"/>, <see cref="UnityResolverExtensions"/>
    /// and, through the adapter's <c>InternalsVisibleTo</c> grant to this assembly, <see cref="ComponentDiscovery"/> and
    /// the internal members of <see cref="InjectionManifest"/>.
    /// </remarks>
    [TestFixture]
    public sealed class ManifestUseTests
    {
        private SceneTestBed _bed;

        /// <summary>
        /// Creates a fresh bed with its own active scene.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            _bed = new SceneTestBed();
            yield return _bed.SetUp("Manifest");
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
        /// An automatic host whose scene manifest is valid, and lists the host itself among its hosts, binds and
        /// initializes the listed components without walking.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenSceneManifestValid_PreparesListedComponentsWithoutWalking()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            CountingInitializableMonoBehaviour i = _bed.Add<CountingInitializableMonoBehaviour>("B", null);
            TestRootScope host = ManifestTestKit.AddInactiveHost(_bed, "Host");
            ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene);
            int walks = ComponentDiscovery.CountWalks();

            host.gameObject.SetActive(true);

            Assert.IsNotNull(c.Service, "The listed component is bound");
            Assert.AreEqual(1, c.InitializeCount, "The listed component is initialized once");
            Assert.AreEqual(1, i.InitializeCount, "The listed initializable is initialized once");
            Assert.AreEqual(walks, ComponentDiscovery.CountWalks(), "Nothing is walked");
            yield return null;
        }

        /// <summary>
        /// A component added to the scene after generation is not in the manifest, so the automatic host leaves it
        /// unbound and uninitialized while it prepares the listed one.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenComponentAddedAfterGeneration_LeavesItUnprepared()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            _bed.Add<CountingInitializableMonoBehaviour>("B", null);
            TestRootScope host = ManifestTestKit.AddInactiveHost(_bed, "Host");
            ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene);
            BoundCountingComponent late = _bed.Add<BoundCountingComponent>("Late", null);

            host.gameObject.SetActive(true);

            Assert.IsNull(late.Service, "The component added after generation is not bound");
            Assert.AreEqual(0, late.InitializeCount, "The component added after generation is not initialized");
            Assert.IsNotNull(c.Service, "The listed component is bound");
            yield return null;
        }

        /// <summary>
        /// An explicit host whose scene manifest is valid prepares the listed component and becomes ready without
        /// walking.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenSceneManifestValid_PreparesWithoutWalking()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);
            int walks = ComponentDiscovery.CountWalks();

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.AreEqual(TaskStatus.RanToCompletion, prepare.Status, "The prepare ran to completion");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The host is ready");
            Assert.IsNotNull(c.Service, "The listed component is bound");
            Assert.AreEqual(1, c.InitializeCount, "The listed component is initialized once");
            Assert.AreEqual(walks, ComponentDiscovery.CountWalks(), "Nothing is walked");
        }

        /// <summary>
        /// Given roots of which two carry hierarchy manifests are prepared together: those two from their manifests,
        /// the third by one walk.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenOnlySomeGivenRootsCarryManifests_WalksOnlyTheOthers()
        {
            GameObject r1 = _bed.Create("R1", null);
            BoundCountingComponent a = r1.AddComponent<BoundCountingComponent>();
            GameObject r2 = _bed.Create("R2", null);
            BoundCountingComponent b = r2.AddComponent<BoundCountingComponent>();
            GameObject r3 = _bed.Create("R3", null);
            BoundCountingComponent d = r3.AddComponent<BoundCountingComponent>();
            ManifestTestKit.AddGenerated(r1, ManifestCoverage.Hierarchy);
            ManifestTestKit.AddGenerated(r2, ManifestCoverage.Hierarchy);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);
            int walks = ComponentDiscovery.CountWalks();

            Task prepare = host.PrepareAsync(new[] { r1, r2, r3 }, CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.AreEqual(TaskStatus.RanToCompletion, prepare.Status, "The prepare ran to completion");
            Assert.IsNotNull(a.Service, "The first manifest root's component is bound");
            Assert.AreEqual(1, a.InitializeCount, "The first manifest root's component is initialized once");
            Assert.IsNotNull(b.Service, "The second manifest root's component is bound");
            Assert.AreEqual(1, b.InitializeCount, "The second manifest root's component is initialized once");
            Assert.IsNotNull(d.Service, "The walked root's component is bound");
            Assert.AreEqual(1, d.InitializeCount, "The walked root's component is initialized once");
            Assert.AreEqual(walks + 1, ComponentDiscovery.CountWalks(), "Only the root without a manifest is walked");
        }

        /// <summary>
        /// <c>InjectGameObject</c> over a root whose hierarchy manifest is valid passes exactly the listed components
        /// to <c>Inject</c>, in order, without walking.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenRootManifestValid_BindsListedComponentsWithoutWalking()
        {
            GameObject root = _bed.Create("R", null);
            InjectableMonoBehaviour x = root.AddComponent<InjectableMonoBehaviour>();
            GameObject child = _bed.Create("Child", root);
            child.AddComponent<PlainMonoBehaviour>();
            InjectableMonoBehaviour y = child.AddComponent<InjectableMonoBehaviour>();
            ManifestTestKit.AddGenerated(root, ManifestCoverage.Hierarchy);
            CountingResolver r = new CountingResolver();
            int walks = ComponentDiscovery.CountWalks();

            r.InjectGameObject(root);

            CollectionAssert.AreEqual(new object[] { x, y }, r.Injected, "Exactly the listed components are bound, in order");
            Assert.AreEqual(walks, ComponentDiscovery.CountWalks(), "Nothing is walked");
            yield return null;
        }

        /// <summary>
        /// <c>InitializeGameObject</c> over a root whose hierarchy manifest is valid initializes each listed
        /// initializable once without walking.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InitializeGameObject_WhenRootManifestValid_InitializesListedComponentsWithoutWalking()
        {
            GameObject root = _bed.Create("R", null);
            CountingInitializableMonoBehaviour p = root.AddComponent<CountingInitializableMonoBehaviour>();
            BoundCountingComponent q = _bed.Add<BoundCountingComponent>("Child", root);
            ManifestTestKit.AddGenerated(root, ManifestCoverage.Hierarchy);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            region.RunInitializables();
            int walks = ComponentDiscovery.CountWalks();

            region.InitializeGameObject(root);

            Assert.AreEqual(1, p.InitializeCount, "The root's initializable is initialized once");
            Assert.AreEqual(1, q.InitializeCount, "The child's initializable is initialized once");
            Assert.AreEqual(walks, ComponentDiscovery.CountWalks(), "Nothing is walked");
            yield return null;
        }

        /// <summary>
        /// An automatic host whose scene manifest is valid and empty builds its resolver, prepares nothing and walks
        /// nothing, and logs nothing.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenManifestValidAndEmpty_PreparesNothingWithoutWalking()
        {
            _bed.Add<PlainMonoBehaviour>("Plain", null);
            TestRootScope host = ManifestTestKit.AddInactiveHost(_bed, "Host");
            InjectionManifest m = ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene);
            CollectionAssert.IsEmpty(m.Inject, "Precondition: the manifest lists nothing");
            int walks = ComponentDiscovery.CountWalks();

            host.gameObject.SetActive(true);

            Assert.IsNotNull(host.Resolver, "The host built its resolver");
            Assert.AreEqual(walks, ComponentDiscovery.CountWalks(), "Nothing is walked");
            LogAssert.NoUnexpectedReceived();
            yield return null;
        }

        /// <summary>
        /// An automatic host without a manifest walks each of its scene's roots once, itself included.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenHostHasNoManifest_WalksEachSceneRootOnce()
        {
            _bed.Add<BoundCountingComponent>("A", null);
            _bed.Add<PlainMonoBehaviour>("B", null);
            int walks = ComponentDiscovery.CountWalks();

            _bed.Add<TestRootScope>("Host", null);

            Assert.AreEqual(walks + 3, ComponentDiscovery.CountWalks(), "Each of the roots A, B and Host is walked once");
            yield return null;
        }

        /// <summary>
        /// An automatic host whose own GameObject carries a hierarchy manifest ignores it and walks its scene as
        /// before.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenHostManifestHasHierarchyCoverage_IgnoresItAndWalks()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            TestRootScope host = ManifestTestKit.AddInactiveHost(_bed, "Host");
            ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Hierarchy);
            int walks = ComponentDiscovery.CountWalks();

            host.gameObject.SetActive(true);

            Assert.IsNotNull(c.Service, "The component is bound");
            Assert.AreEqual(1, c.InitializeCount, "The component is initialized once");
            Assert.AreEqual(walks + 2, ComponentDiscovery.CountWalks(), "Both scene roots are walked");
            yield return null;
        }

        /// <summary>
        /// <c>InjectGameObject</c> over a root carrying a scene manifest ignores it, so the other root it lists is not
        /// bound, and walks the root once.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenRootManifestHasSceneCoverage_IgnoresItAndWalks()
        {
            GameObject root = _bed.Create("R", null);
            InjectableMonoBehaviour x = root.AddComponent<InjectableMonoBehaviour>();
            _bed.Add<InjectableMonoBehaviour>("S", null);
            ManifestTestKit.AddGenerated(root, ManifestCoverage.Scene);
            CountingResolver r = new CountingResolver();
            int walks = ComponentDiscovery.CountWalks();

            r.InjectGameObject(root);

            CollectionAssert.AreEqual(new object[] { x }, r.Injected, "Only the root's own walk is bound");
            Assert.AreEqual(walks + 1, ComponentDiscovery.CountWalks(), "The root is walked once");
            yield return null;
        }

        /// <summary>
        /// <c>InstantiateAndInject</c> of a root carrying a hierarchy manifest binds the clone's components from the
        /// clone's own manifest, whose entries are all beneath the clone, and leaves the source unbound, without
        /// walking.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InstantiateAndInject_WhenSourceRootHasManifest_BindsTheCloneNotTheSource()
        {
            GameObject source = _bed.Create("Source", null);
            BoundCountingComponent s = source.AddComponent<BoundCountingComponent>();
            InjectableMonoBehaviour t = _bed.Add<InjectableMonoBehaviour>("Child", source);
            ManifestTestKit.AddGenerated(source, ManifestCoverage.Hierarchy);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            int walks = ComponentDiscovery.CountWalks();

            BoundCountingComponent clone = region.InstantiateAndInject(s);

            Assert.IsNotNull(clone.Service, "The clone's root component is bound");
            Assert.IsNotNull(clone.GetComponentInChildren<InjectableMonoBehaviour>(true).Service, "The clone's child component is bound");
            Assert.IsNull(s.Service, "The source's root component is not bound");
            Assert.IsNull(t.Service, "The source's child component is not bound");
            IReadOnlyList<MonoBehaviour> entries = clone.GetComponent<InjectionManifest>().Inject;
            for (int i = 0; i < entries.Count; i++)
            {
                Assert.IsTrue(entries[i].transform.IsChildOf(clone.transform), "Each entry of the clone's manifest is beneath the clone");
            }

            Assert.AreEqual(walks, ComponentDiscovery.CountWalks(), "Nothing is walked");
            yield return null;
        }

        /// <summary>
        /// Two clones of one source each carry a manifest listing only their own components, so the four bound
        /// components are exactly the two clones' entries and never the source's.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InstantiateAndInject_WhenTwoClonesMade_EachManifestListsOnlyItsOwnComponents()
        {
            GameObject source = _bed.Create("Source", null);
            BoundCountingComponent s = source.AddComponent<BoundCountingComponent>();
            InjectableMonoBehaviour t = _bed.Add<InjectableMonoBehaviour>("Child", source);
            ManifestTestKit.AddGenerated(source, ManifestCoverage.Hierarchy);
            CountingResolver r = new CountingResolver();

            BoundCountingComponent first = r.InstantiateAndInject(s);
            BoundCountingComponent second = r.InstantiateAndInject(s);

            IReadOnlyList<MonoBehaviour> firstEntries = first.GetComponent<InjectionManifest>().Inject;
            IReadOnlyList<MonoBehaviour> secondEntries = second.GetComponent<InjectionManifest>().Inject;
            List<object> expected = new List<object>();
            for (int i = 0; i < firstEntries.Count; i++)
            {
                Assert.IsTrue(firstEntries[i].transform.IsChildOf(first.transform), "Each entry of the first clone's manifest is beneath the first clone");
                Assert.AreNotSame(s, firstEntries[i], "The first clone's manifest does not list the source's root component");
                Assert.AreNotSame(t, firstEntries[i], "The first clone's manifest does not list the source's child component");
                expected.Add(firstEntries[i]);
            }

            for (int i = 0; i < secondEntries.Count; i++)
            {
                Assert.IsTrue(secondEntries[i].transform.IsChildOf(second.transform), "Each entry of the second clone's manifest is beneath the second clone");
                Assert.AreNotSame(s, secondEntries[i], "The second clone's manifest does not list the source's root component");
                Assert.AreNotSame(t, secondEntries[i], "The second clone's manifest does not list the source's child component");
                expected.Add(secondEntries[i]);
            }

            CollectionAssert.AreEquivalent(expected, r.Injected, "Exactly the two clones' entries are bound");
            Assert.AreEqual(4, r.Injected.Count, "Two components per clone are bound");
            yield return null;
        }

        /// <summary>
        /// A disabled manifest on an inactive root still counts: <c>InjectGameObject</c> binds exactly its listed
        /// components without walking.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenManifestDisabledOnInactiveRoot_StillUsesIt()
        {
            GameObject root = _bed.Create("R", null);
            InjectableMonoBehaviour x = root.AddComponent<InjectableMonoBehaviour>();
            InjectableMonoBehaviour y = _bed.Add<InjectableMonoBehaviour>("Child", root);
            InjectionManifest m = ManifestTestKit.AddGenerated(root, ManifestCoverage.Hierarchy);
            m.enabled = false;
            root.SetActive(false);
            CountingResolver r = new CountingResolver();
            int walks = ComponentDiscovery.CountWalks();

            r.InjectGameObject(root);

            CollectionAssert.AreEqual(new object[] { x, y }, r.Injected, "Exactly the listed components are bound");
            Assert.AreEqual(walks, ComponentDiscovery.CountWalks(), "Nothing is walked");
            yield return null;
        }
    }
}
