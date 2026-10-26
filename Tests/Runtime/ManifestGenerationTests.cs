using System.Collections;
using Didionysymus.Lattice.Runtime.Unity;
using Didionysymus.Lattice.Tests.Runtime.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Didionysymus.Lattice.Tests.Runtime
{
    /// <summary>
    /// Pins how a manifest is filled:
    /// <list type="bullet">
    ///     <item>Scene coverage lists what the host's scene batch discovers, in the same order: inactive objects included, a boundary on a scene root and every boundary subtree excluded</item>
    ///     <item>Hierarchy coverage lists what a walk of the manifest's GameObject as a given root discovers, in the same order: the root claims its own boundary, boundaries beneath it are excluded</item>
    ///     <item>Every host the walk passes is recorded in walk order, a host inside a boundary excluded, and generation never refuses for ownership</item>
    ///     <item>A unit with nothing eligible yields a valid empty manifest: the current format, empty lists, the hosts found</item>
    /// </list>
    /// Discovery's lists are collected before the manifest is added, so the comparison never reads the manifest
    /// itself. Every scene and GameObject is runtime-created.
    /// </summary>
    /// <remarks>
    /// Exercises, through the adapter's <c>InternalsVisibleTo</c> grant to this assembly, the internal
    /// <see cref="ComponentDiscovery.Generate"/>, <see cref="ComponentDiscovery.CollectBatch"/>,
    /// <see cref="ComponentDiscovery.CollectRoot"/> and the internal members of <see cref="InjectionManifest"/>.
    /// </remarks>
    [TestFixture]
    public sealed class ManifestGenerationTests
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
        /// A scene manifest lists exactly what the host's scene batch discovers, in order: the inactive descendant is
        /// included, the boundary subtree and the scene root carrying a boundary are excluded. It also writes an empty
        /// async list, the current format and scene coverage.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Generate_WhenSceneCoverage_MatchesSceneDiscoveryInOrder()
        {
            GameObject a = _bed.Create("A", null);
            BoundCountingComponent a1 = a.AddComponent<BoundCountingComponent>();
            a.AddComponent<PlainMonoBehaviour>();
            GameObject inactive = _bed.Create("Inactive", a);
            InjectableMonoBehaviour a2 = inactive.AddComponent<InjectableMonoBehaviour>();
            inactive.SetActive(false);
            GameObject boundary = _bed.Create("Boundary", a);
            boundary.AddComponent<ScopeBoundary>();
            _bed.Add<BoundCountingComponent>("Inside", boundary);
            CountingInitializableMonoBehaviour b1 = _bed.Add<CountingInitializableMonoBehaviour>("B", null);
            GameObject c = _bed.Create("C", null);
            c.AddComponent<ScopeBoundary>();
            c.AddComponent<BoundCountingComponent>();
            TestRootScope host = ManifestTestKit.AddInactiveHost(_bed, "Host");
            MonoBehaviour[] discoveredInject;
            MonoBehaviour[] discoveredInitialize;
            InjectionTargets t = ComponentDiscovery.Rent();
            try
            {
                ComponentDiscovery.CollectBatch(ScopeHostEngine.GetSceneRoots(host.gameObject), DiscoveryBatch.SceneRoots, host, t);
                discoveredInject = t.Inject.ToArray();
                discoveredInitialize = t.Initialize.ToArray();
            }
            finally
            {
                ComponentDiscovery.Return(t);
            }

            InjectionManifest m = ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene);

            CollectionAssert.AreEqual(new MonoBehaviour[] { a1, a2 }, m.Inject, "The inject list holds the bound components outside boundaries, inactive included, in walk order");
            CollectionAssert.AreEqual(new MonoBehaviour[] { a1, b1 }, m.Initialize, "The initialize list holds the initializables outside boundaries, in walk order");
            CollectionAssert.AreEqual(discoveredInject, m.Inject, "The inject list equals the scene batch's discovery");
            CollectionAssert.AreEqual(discoveredInitialize, m.Initialize, "The initialize list equals the scene batch's discovery");
            CollectionAssert.IsEmpty(m.AsyncStart, "The async list is written empty");
            Assert.AreEqual(InjectionManifest.CurrentFormat, m.Format, "The current format is written");
            Assert.AreEqual(ManifestCoverage.Scene, m.Coverage, "Scene coverage is written");
            yield return null;
        }

        /// <summary>
        /// A hierarchy manifest lists exactly what a walk of its GameObject as a given root discovers, in order: the
        /// root claims its own boundary, the nested boundary's subtree is excluded. It writes hierarchy coverage.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Generate_WhenHierarchyCoverage_MatchesRootDiscoveryInOrder()
        {
            GameObject r = _bed.Create("R", null);
            r.AddComponent<ScopeBoundary>();
            InjectableMonoBehaviour r1 = r.AddComponent<InjectableMonoBehaviour>();
            GameObject nested = _bed.Create("Nested", r);
            nested.AddComponent<ScopeBoundary>();
            nested.AddComponent<InjectableMonoBehaviour>();
            GameObject leaf = _bed.Create("Leaf", r);
            CountingInitializableMonoBehaviour p1 = leaf.AddComponent<CountingInitializableMonoBehaviour>();
            BoundCountingComponent p2 = leaf.AddComponent<BoundCountingComponent>();
            MonoBehaviour[] discoveredInject;
            MonoBehaviour[] discoveredInitialize;
            InjectionTargets t = ComponentDiscovery.Rent();
            try
            {
                ComponentDiscovery.CollectRoot(r, t);
                discoveredInject = t.Inject.ToArray();
                discoveredInitialize = t.Initialize.ToArray();
            }
            finally
            {
                ComponentDiscovery.Return(t);
            }

            InjectionManifest m = ManifestTestKit.AddGenerated(r, ManifestCoverage.Hierarchy);

            CollectionAssert.AreEqual(new MonoBehaviour[] { r1, p2 }, m.Inject, "The inject list holds the root's own component and the leaf's, not the nested boundary's");
            CollectionAssert.AreEqual(new MonoBehaviour[] { p1, p2 }, m.Initialize, "The initialize list holds the leaf's initializables in walk order");
            CollectionAssert.AreEqual(discoveredInject, m.Inject, "The inject list equals the root's discovery");
            CollectionAssert.AreEqual(discoveredInitialize, m.Initialize, "The initialize list equals the root's discovery");
            Assert.AreEqual(ManifestCoverage.Hierarchy, m.Coverage, "Hierarchy coverage is written");
            yield return null;
        }

        /// <summary>
        /// A scene unit holding two hosts outside boundaries and a third inside one generates without refusing and
        /// records the two outside, in walk order.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Generate_WhenUnitHoldsSeveralHosts_RecordsEachHostOutsideBoundariesInWalkOrder()
        {
            TestRootScope host = ManifestTestKit.AddInactiveHost(_bed, "Host");
            ExplicitScopeHost other = _bed.Add<ExplicitScopeHost>("Other", null);
            GameObject boundary = _bed.Create("Boundary", null);
            boundary.AddComponent<ScopeBoundary>();
            _bed.Add<ExplicitScopeHost>("Inner", boundary);
            InjectionManifest m = null;

            Assert.DoesNotThrow(() => { m = ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene); }, "Generation never refuses for ownership");

            CollectionAssert.AreEqual(new MonoBehaviour[] { host, other }, m.Hosts, "Both hosts outside the boundary are recorded in walk order; the one inside is not");
            yield return null;
        }

        /// <summary>
        /// A scene unit with nothing eligible yields a valid empty manifest: the current format, three empty lists, and
        /// the host itself recorded.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Generate_WhenNothingIsEligible_WritesValidEmptyManifest()
        {
            _bed.Add<PlainMonoBehaviour>("Plain", null);
            TestRootScope host = ManifestTestKit.AddInactiveHost(_bed, "Host");

            InjectionManifest m = ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene);

            Assert.AreEqual(InjectionManifest.CurrentFormat, m.Format, "The current format is written, so the manifest is generated");
            CollectionAssert.IsEmpty(m.Inject, "Nothing is listed for binding");
            CollectionAssert.IsEmpty(m.Initialize, "Nothing is listed for initializing");
            CollectionAssert.IsEmpty(m.AsyncStart, "The async list is empty");
            CollectionAssert.AreEqual(new MonoBehaviour[] { host }, m.Hosts, "The host the walk passes is recorded");
            yield return null;
        }
    }
}
