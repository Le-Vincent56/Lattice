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
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Didionysymus.Lattice.Tests.Runtime
{
    /// <summary>
    /// Pins how an unusable manifest is refused and what a manifest changes about ownership:
    /// <list type="bullet">
    ///     <item>A manifest never generated, of a newer format or with unknown coverage, or with an entry that is destroyed, has lost its list's role, has left the host's scene or the root, or now sits inside a <see cref="ScopeBoundary"/>, is refused with <see cref="InjectionManifestException"/> before anything is bound; the format is read before the coverage, so a newer manifest of the other coverage is refused, not ignored; each entry reports only its first failing check</item>
    ///     <item>The automatic host throws it from <c>Awake</c>; the explicit host faults its prepare, changes nothing and stays attached, so a regenerated manifest then prepares; the extensions throw it</item>
    ///     <item>Every unusable manifest of a batch is reported in one exception, in root order, ahead of any ownership problem</item>
    ///     <item>With <see cref="InjectionManifest.AllowDevelopmentFallback"/> set, an unusable manifest is logged as one warning and its unit walked instead, each component bound once</item>
    ///     <item>A manifest's recorded hosts take the walk's place in the host check: a live other host refuses, a destroyed one does not; the overlap and nested-host checks run as before, without a walk</item>
    ///     <item>Every refusal returns its discovery rentals, and a used manifest leaves the pooled collections empty</item>
    /// </list>
    /// Messages name each object as <c>{scene}/{path}</c>. "Walks" and "rentals" are before/after deltas read just
    /// before the act. Every scene and GameObject is runtime-created; tasks are driven by yielding frames until they
    /// complete or 10 s of real time pass. An exception thrown from an <c>Awake</c> that activation runs is logged by
    /// Unity, not thrown to the caller.
    /// </summary>
    /// <remarks>
    /// Exercises <see cref="LifetimeScopeBehaviour"/>, <see cref="ExplicitScopeHost"/>, <see cref="UnityResolverExtensions"/>,
    /// <see cref="InjectionManifestException"/>, <see cref="ScopeOwnershipException"/> and, through the adapter's
    /// <c>InternalsVisibleTo</c> grant to this assembly, <see cref="ComponentDiscovery"/> and the internal members of
    /// <see cref="InjectionManifest"/>. The fallback flag is cleared before and after every test.
    /// </remarks>
    [TestFixture]
    public sealed class ManifestValidityTests
    {
        private SceneTestBed _bed;

        /// <summary>
        /// Clears the fallback flag and creates a fresh bed with its own active scene.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            InjectionManifest.AllowDevelopmentFallback = false;
            _bed = new SceneTestBed();
            yield return _bed.SetUp("Manifest");
        }

        /// <summary>
        /// Clears the fallback flag and tears the bed down.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            InjectionManifest.AllowDevelopmentFallback = false;
            yield return _bed.TearDown();
        }

        /// <summary>
        /// An automatic host whose manifest was never generated refuses from <c>Awake</c>: Unity logs the
        /// <see cref="InjectionManifestException"/>, and nothing is bound or initialized.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenManifestNeverGenerated_LogsInjectionManifestExceptionAndBindsNothing()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            TestRootScope host = ManifestTestKit.AddInactiveHost(_bed, "Host");
            ManifestTestKit.AddNeverGenerated(host.gameObject);
            LogAssert.Expect(LogType.Exception, new Regex(@"InjectionManifestException: The InjectionManifest on .*/Host cannot be used: it was never generated\."));

            host.gameObject.SetActive(true);

            Assert.IsNull(c.Service, "Nothing is bound");
            Assert.AreEqual(0, c.InitializeCount, "Nothing is initialized");
            LogAssert.NoUnexpectedReceived();
            yield return null;
        }

        /// <summary>
        /// An explicit host whose manifest was never generated faults its prepare with the exact refusal message,
        /// changes nothing and stays attached.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenManifestNeverGenerated_RefusesAndStaysAttached()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            ManifestTestKit.AddNeverGenerated(host.gameObject);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<InjectionManifestException>(prepare.Exception.InnerException, "The manifest is refused");
            Assert.AreEqual(Unusable($"{_bed.Scene.name}/Host", "it was never generated"), prepare.Exception.InnerException.Message, "The message names the manifest, the problem and the fix");
            AssertUnchanged(host, region);
            Assert.IsNull(c.Service, "Nothing is bound");
        }

        /// <summary>
        /// A listed component destroyed after generation makes the manifest unusable, naming both entries it held;
        /// the live entry is not bound either.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenListedComponentDestroyed_RefusesNamingTheEntry()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            BoundCountingComponent d = _bed.Add<BoundCountingComponent>("B", null);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene);
            Object.DestroyImmediate(c);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<InjectionManifestException>(prepare.Exception.InnerException, "The manifest is refused");
            string problems = "Inject[0] is missing (destroyed, or its script is missing); Initialize[0] is missing (destroyed, or its script is missing)";
            Assert.AreEqual(Unusable($"{_bed.Scene.name}/Host", problems), prepare.Exception.InnerException.Message, "The message names each missing entry");
            Assert.IsNull(d.Service, "The live entry is not bound");
            AssertUnchanged(host, region);
        }

        /// <summary>
        /// Entries whose types lack their list's role make the manifest unusable, each named with its type.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenListedComponentLacksTheListRole_RefusesNamingEachEntry()
        {
            PlainMonoBehaviour p = _bed.Add<PlainMonoBehaviour>("P", null);
            InjectableMonoBehaviour q = _bed.Add<InjectableMonoBehaviour>("Q", null);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            ManifestTestKit.AddWithContent(host.gameObject, ManifestCoverage.Scene, new MonoBehaviour[] { p }, new MonoBehaviour[] { q });
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<InjectionManifestException>(prepare.Exception.InnerException, "The manifest is refused");
            string problems = "Inject[0] (PlainMonoBehaviour) has no [Inject] members; Initialize[0] (InjectableMonoBehaviour) is not an IInitializable";
            Assert.AreEqual(Unusable($"{_bed.Scene.name}/Host", problems), prepare.Exception.InnerException.Message, "The message names each entry, its type and the role it lacks");
            Assert.IsNull(q.Service, "Nothing is bound");
            AssertUnchanged(host, region);
        }

        /// <summary>
        /// A listed component moved to another scene after generation makes the scene manifest unusable, naming the
        /// entry in both lists and the host's scene.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenListedComponentMovedToAnotherScene_Refuses()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene);
            Scene other = _bed.CreateExtraScene("Session");
            SceneManager.MoveGameObjectToScene(c.gameObject, other);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<InjectionManifestException>(prepare.Exception.InnerException, "The manifest is refused");
            string scene = _bed.Scene.name;
            string message = prepare.Exception.InnerException.Message;
            StringAssert.Contains($"Inject[0] ({other.name}/A) is not in scene '{scene}'", message, "The inject entry is named outside the scene");
            StringAssert.Contains($"Initialize[0] ({other.name}/A) is not in scene '{scene}'", message, "The initialize entry is named outside the scene");
            Assert.IsNull(c.Service, "Nothing is bound");
            AssertUnchanged(host, region);
        }

        /// <summary>
        /// A listed child moved out from beneath the root makes the hierarchy manifest unusable, so
        /// <c>InjectGameObject</c> throws naming the entry and the root, and binds nothing.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenListedComponentNoLongerBeneathRoot_ThrowsAndBindsNothing()
        {
            GameObject root = _bed.Create("R", null);
            root.AddComponent<InjectableMonoBehaviour>();
            InjectableMonoBehaviour x = _bed.Add<InjectableMonoBehaviour>("Child", root);
            ManifestTestKit.AddGenerated(root, ManifestCoverage.Hierarchy);
            x.transform.SetParent(null);
            CountingResolver r = new CountingResolver();

            InjectionManifestException e = Assert.Throws<InjectionManifestException>(() => r.InjectGameObject(root));

            string scene = _bed.Scene.name;
            StringAssert.Contains($"Inject[1] ({scene}/Child) is not beneath {scene}/R", e.Message, "The entry and the root are named");
            CollectionAssert.IsEmpty(r.Injected, "Nothing is bound");
            yield return null;
        }

        /// <summary>
        /// A manifest of a format newer than this version reads is refused, naming both formats.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenManifestFormatNewer_Refuses()
        {
            _bed.Add<BoundCountingComponent>("A", null);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            ManifestTestKit.AddNeverGenerated(host.gameObject).SetFormat(2);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<InjectionManifestException>(prepare.Exception.InnerException, "The manifest is refused");
            string problems = "its format 2 is newer than this version of Lattice reads (1)";
            Assert.AreEqual(Unusable($"{_bed.Scene.name}/Host", problems), prepare.Exception.InnerException.Message, "The message names both formats");
            AssertUnchanged(host, region);
        }

        /// <summary>
        /// A refused prepare changes nothing, so once the manifest is regenerated the next prepare succeeds and binds
        /// and initializes the scene once.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenManifestRegeneratedAfterRefusal_Prepares()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            InjectionManifest m = ManifestTestKit.AddNeverGenerated(host.gameObject);
            host.Attach(ManifestTestKit.CreateRegion(_bed), ScopeOwnership.Owned);
            Task first = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(first);
            Assert.IsTrue(first.IsFaulted, "Precondition: the first prepare faults");
            Assert.IsInstanceOf<InjectionManifestException>(first.Exception.InnerException, "Precondition: the first prepare refuses the manifest");
            ComponentDiscovery.Generate(m, ManifestCoverage.Scene);

            Task second = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(second);

            Assert.AreEqual(TaskStatus.RanToCompletion, second.Status, "The second prepare ran to completion");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The host is ready");
            Assert.IsNotNull(c.Service, "The component is bound");
            Assert.AreEqual(1, c.InitializeCount, "The component is initialized once");
        }

        /// <summary>
        /// Two unusable root manifests in one given-roots batch are reported in one exception, one line each, in
        /// root order.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenTwoGivenRootManifestsInvalid_ReportsBothInOneException()
        {
            GameObject r1 = _bed.Create("R1", null);
            r1.AddComponent<BoundCountingComponent>();
            GameObject r2 = _bed.Create("R2", null);
            r2.AddComponent<BoundCountingComponent>();
            ManifestTestKit.AddNeverGenerated(r1);
            ManifestTestKit.AddNeverGenerated(r2).SetFormat(2);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(new[] { r1, r2 }, CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<InjectionManifestException>(prepare.Exception.InnerException, "The manifests are refused");
            string scene = _bed.Scene.name;
            string expected = Unusable($"{scene}/R1", "it was never generated") + "\n" + Unusable($"{scene}/R2", "its format 2 is newer than this version of Lattice reads (1)");
            Assert.AreEqual(expected, prepare.Exception.InnerException.Message, "Both manifests are reported, one line each, in root order");
            AssertUnchanged(host, region);
        }

        /// <summary>
        /// <c>InitializeGameObject</c> over a root whose manifest was never generated throws before initializing
        /// anything.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InitializeGameObject_WhenManifestNeverGenerated_ThrowsBeforeInitializing()
        {
            GameObject root = _bed.Create("R", null);
            CountingInitializableMonoBehaviour p = root.AddComponent<CountingInitializableMonoBehaviour>();
            ManifestTestKit.AddNeverGenerated(root);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            region.RunInitializables();

            Assert.Throws<InjectionManifestException>(() => region.InitializeGameObject(root));

            Assert.AreEqual(0, p.InitializeCount, "Nothing is initialized");
            yield return null;
        }

        /// <summary>
        /// The initialize list is validated too: an entry listed only there, destroyed after generation, makes the
        /// manifest unusable.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenInitializeEntryDestroyed_Refuses()
        {
            CountingInitializableMonoBehaviour p = _bed.Add<CountingInitializableMonoBehaviour>("P", null);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            InjectionManifest m = ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene);
            CollectionAssert.IsEmpty(m.Inject, "Precondition: the entry is listed for initializing only");
            Object.DestroyImmediate(p);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<InjectionManifestException>(prepare.Exception.InnerException, "The manifest is refused");
            string problems = "Initialize[0] is missing (destroyed, or its script is missing)";
            Assert.AreEqual(Unusable($"{_bed.Scene.name}/Host", problems), prepare.Exception.InnerException.Message, "The message names the missing initialize entry");
            AssertUnchanged(host, region);
        }

        /// <summary>
        /// A batch with an unusable manifest and a second host is refused for the manifest alone: ownership read from
        /// an unusable manifest is not reported.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenManifestInvalidAndHostsConflict_ReportsTheManifestFirst()
        {
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            _bed.Add<ExplicitScopeHost>("Other", null);
            ManifestTestKit.AddNeverGenerated(host.gameObject).SetFormat(2);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<InjectionManifestException>(prepare.Exception.InnerException, "The manifest is refused, not the ownership");
            StringAssert.DoesNotContain("More than one scope host", prepare.Exception.InnerException.Message, "The host conflict is not reported");
            AssertUnchanged(host, region);
        }

        /// <summary>
        /// A current-format manifest whose coverage is neither scene nor hierarchy holds corrupt data and is refused,
        /// naming both raw values.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenManifestCoverageUnknown_Refuses()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            ManifestTestKit.AddWithContent(host.gameObject, (ManifestCoverage)7, new MonoBehaviour[] { c }, Array.Empty<MonoBehaviour>());
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<InjectionManifestException>(prepare.Exception.InnerException, "The manifest is refused");
            string problems = "its data is not valid (format 1, coverage 7)";
            Assert.AreEqual(Unusable($"{_bed.Scene.name}/Host", problems), prepare.Exception.InnerException.Message, "The message names the raw format and coverage");
            Assert.IsNull(c.Service, "Nothing is bound");
            AssertUnchanged(host, region);
        }

        /// <summary>
        /// A listed component moved beneath a <see cref="ScopeBoundary"/> scene root after generation makes the scene
        /// manifest unusable, naming each entry and the boundary, so the boundary's scope stays its only owner.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenListedComponentMovedInsideBoundary_RefusesNamingTheBoundary()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            GameObject boundary = _bed.Create("Boundary", null);
            boundary.AddComponent<ScopeBoundary>();
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene);
            c.transform.SetParent(boundary.transform);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<InjectionManifestException>(prepare.Exception.InnerException, "The manifest is refused");
            string scene = _bed.Scene.name;
            string problems = $"Inject[0] ({scene}/Boundary/A) is inside the ScopeBoundary on {scene}/Boundary; Initialize[0] ({scene}/Boundary/A) is inside the ScopeBoundary on {scene}/Boundary";
            Assert.AreEqual(Unusable($"{scene}/Host", problems), prepare.Exception.InnerException.Message, "The message names each entry and the boundary");
            Assert.IsNull(c.Service, "Nothing is bound");
            Assert.AreEqual(0, c.InitializeCount, "Nothing is initialized");
            AssertUnchanged(host, region);
        }

        /// <summary>
        /// A listed child moved beneath a nested boundary makes the hierarchy manifest unusable for that child only:
        /// the root's own boundary is claimed, so the root's entry is not reported.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenListedChildMovedBeneathNestedBoundary_ThrowsForThatChildOnly()
        {
            GameObject root = _bed.Create("R", null);
            root.AddComponent<ScopeBoundary>();
            root.AddComponent<InjectableMonoBehaviour>();
            GameObject leaf = _bed.Create("Leaf", root);
            leaf.AddComponent<InjectableMonoBehaviour>();
            GameObject nested = _bed.Create("Nested", root);
            nested.AddComponent<ScopeBoundary>();
            InjectionManifest m = ManifestTestKit.AddGenerated(root, ManifestCoverage.Hierarchy);
            Assert.AreEqual(2, m.Inject.Count, "Precondition: the root's and the leaf's components are listed");
            leaf.transform.SetParent(nested.transform);
            CountingResolver r = new CountingResolver();

            InjectionManifestException e = Assert.Throws<InjectionManifestException>(() => r.InjectGameObject(root));

            string scene = _bed.Scene.name;
            StringAssert.Contains($"Inject[1] ({scene}/R/Nested/Leaf) is inside the ScopeBoundary on {scene}/R/Nested", e.Message, "The moved child and the nested boundary are named");
            StringAssert.DoesNotContain("Inject[0]", e.Message, "The root claims its own boundary, so its entry is not reported");
            CollectionAssert.IsEmpty(r.Injected, "Nothing is bound");
            yield return null;
        }

        /// <summary>
        /// The format is read before the coverage: a manifest of a newer format is refused by the unit that finds it,
        /// even when it claims the other coverage, which a current-format manifest would have the unit ignore.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenNewerFormatManifestHasOtherCoverage_RefusesRatherThanIgnores()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            ManifestTestKit.AddWithContent(host.gameObject, ManifestCoverage.Hierarchy, new MonoBehaviour[] { c }, Array.Empty<MonoBehaviour>()).SetFormat(2);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults instead of ignoring the manifest and walking");
            Assert.IsInstanceOf<InjectionManifestException>(prepare.Exception.InnerException, "The manifest is refused");
            string problems = "its format 2 is newer than this version of Lattice reads (1)";
            Assert.AreEqual(Unusable($"{_bed.Scene.name}/Host", problems), prepare.Exception.InnerException.Message, "The newer format is reported, not the coverage");
            Assert.IsNull(c.Service, "Nothing is bound");
            AssertUnchanged(host, region);
        }

        /// <summary>
        /// An entry that fails several checks reports only its first: a component without <c>[Inject]</c> members
        /// moved to another scene is named for its missing role alone.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenEntryFailsSeveralChecks_ReportsOnlyItsFirst()
        {
            PlainMonoBehaviour p = _bed.Add<PlainMonoBehaviour>("P", null);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            ManifestTestKit.AddWithContent(host.gameObject, ManifestCoverage.Scene, new MonoBehaviour[] { p }, Array.Empty<MonoBehaviour>());
            Scene other = _bed.CreateExtraScene("Session");
            SceneManager.MoveGameObjectToScene(p.gameObject, other);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<InjectionManifestException>(prepare.Exception.InnerException, "The manifest is refused");
            string problems = "Inject[0] (PlainMonoBehaviour) has no [Inject] members";
            Assert.AreEqual(Unusable($"{_bed.Scene.name}/Host", problems), prepare.Exception.InnerException.Message, "Only the first failing check, the role, is reported; the scene is not");
            AssertUnchanged(host, region);
        }

        /// <summary>
        /// With the development fallback set, an automatic host whose manifest was never generated logs one warning
        /// and walks its scene instead, preparing it as without a manifest.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenManifestInvalidAndFallbackAllowed_WalksAndLogsWarning()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            TestRootScope host = ManifestTestKit.AddInactiveHost(_bed, "Host");
            ManifestTestKit.AddNeverGenerated(host.gameObject);
            InjectionManifest.AllowDevelopmentFallback = true;
            LogAssert.Expect(LogType.Warning, new Regex(@"cannot be used: it was never generated\..* Walked the hierarchy instead, because InjectionManifest\.AllowDevelopmentFallback is set\."));
            int walks = ComponentDiscovery.CountWalks();

            host.gameObject.SetActive(true);

            Assert.IsNotNull(c.Service, "The component is bound");
            Assert.AreEqual(1, c.InitializeCount, "The component is initialized once");
            Assert.AreEqual(walks + 2, ComponentDiscovery.CountWalks(), "Both scene roots are walked");
            LogAssert.NoUnexpectedReceived();
            yield return null;
        }

        /// <summary>
        /// With the development fallback set, <c>InjectGameObject</c> over a root whose listed child was destroyed
        /// logs one warning and walks the root instead of the manifest, binding each live component exactly once.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenManifestInvalidAndFallbackAllowed_WalksAndLogsWarning()
        {
            GameObject root = _bed.Create("R", null);
            InjectableMonoBehaviour x = root.AddComponent<InjectableMonoBehaviour>();
            InjectableMonoBehaviour y = _bed.Add<InjectableMonoBehaviour>("C1", root);
            InjectableMonoBehaviour z = _bed.Add<InjectableMonoBehaviour>("C2", root);
            ManifestTestKit.AddGenerated(root, ManifestCoverage.Hierarchy);
            Object.DestroyImmediate(z);
            InjectionManifest.AllowDevelopmentFallback = true;
            LogAssert.Expect(LogType.Warning, new Regex(@"Inject\[2\] is missing"));
            CountingResolver r = new CountingResolver();

            r.InjectGameObject(root);

            CollectionAssert.AreEqual(new object[] { x, y }, r.Injected, "Each live component is bound exactly once");
            LogAssert.NoUnexpectedReceived();
            yield return null;
        }

        /// <summary>
        /// An automatic host whose valid manifest records another live host refuses from <c>Awake</c> with the
        /// ownership message, naming both, without walking and without binding.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenManifestRecordsAnotherHost_LogsScopeOwnershipExceptionAndBindsNothing()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            TestRootScope host = ManifestTestKit.AddInactiveHost(_bed, "Host");
            _bed.Add<ExplicitScopeHost>("Other", null);
            ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene);
            LogAssert.Expect(LogType.Exception, new Regex(@"ScopeOwnershipException: More than one scope host owns these objects: .*/Host; .*/Other\."));
            int walks = ComponentDiscovery.CountWalks();

            host.gameObject.SetActive(true);

            Assert.IsNull(c.Service, "Nothing is bound");
            Assert.AreEqual(walks, ComponentDiscovery.CountWalks(), "Nothing is walked");
            LogAssert.NoUnexpectedReceived();
            yield return null;
        }

        /// <summary>
        /// A host recorded in the manifest and destroyed since is skipped, so the explicit host prepares from its
        /// manifest without walking.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenRecordedHostDestroyed_Prepares()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            ExplicitScopeHost other = _bed.Add<ExplicitScopeHost>("Other", null);
            ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene);
            Object.DestroyImmediate(other.gameObject);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);
            int walks = ComponentDiscovery.CountWalks();

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.AreEqual(TaskStatus.RanToCompletion, prepare.Status, "The prepare ran to completion");
            Assert.AreEqual(ScopeHostState.Ready, host.State, "The host is ready");
            Assert.IsNotNull(c.Service, "The component is bound");
            Assert.AreEqual(walks, ComponentDiscovery.CountWalks(), "Nothing is walked");
        }

        /// <summary>
        /// Overlapping given roots that both carry valid manifests are refused with the overlap message, without
        /// walking, and the host stays attached.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenGivenRootsWithManifestsOverlap_RefusesWithScopeOwnershipException()
        {
            GameObject r1 = _bed.Create("R1", null);
            r1.AddComponent<BoundCountingComponent>();
            GameObject r2 = _bed.Create("R2", r1);
            r2.AddComponent<BoundCountingComponent>();
            ManifestTestKit.AddGenerated(r1, ManifestCoverage.Hierarchy);
            ManifestTestKit.AddGenerated(r2, ManifestCoverage.Hierarchy);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            host.Attach(region, ScopeOwnership.Owned);
            int walks = ComponentDiscovery.CountWalks();

            Task prepare = host.PrepareAsync(new[] { r1, r2 }, CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<ScopeOwnershipException>(prepare.Exception.InnerException, "It is refused for ownership");
            string scene = _bed.Scene.name;
            StringAssert.Contains($"The given roots overlap: {scene}/R1/R2 is beneath {scene}/R1. Give each root once.", prepare.Exception.InnerException.Message, "The message names the inner root and its ancestor");
            Assert.AreEqual(walks, ComponentDiscovery.CountWalks(), "Nothing is walked");
            AssertUnchanged(host, region);
        }

        /// <summary>
        /// An automatic host inside a boundary refuses from <c>Awake</c> even when its scene manifest is valid, without
        /// walking and without binding.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenHostWithValidManifestSitsInsideBoundary_LogsScopeOwnershipException()
        {
            BoundCountingComponent c = _bed.Add<BoundCountingComponent>("A", null);
            GameObject boundary = _bed.Create("Boundary", null);
            boundary.AddComponent<ScopeBoundary>();
            TestRootScope host = ManifestTestKit.AddInactiveHost(_bed, "Host");
            host.transform.SetParent(boundary.transform);
            ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene);
            LogAssert.Expect(LogType.Exception, new Regex("A LifetimeScopeBehaviour cannot sit inside a ScopeBoundary"));
            int walks = ComponentDiscovery.CountWalks();

            host.gameObject.SetActive(true);

            Assert.IsNull(c.Service, "Nothing is bound");
            Assert.AreEqual(walks, ComponentDiscovery.CountWalks(), "Nothing is walked");
            LogAssert.NoUnexpectedReceived();
            yield return null;
        }

        /// <summary>
        /// An automatic host's manifest refusal returns every discovery rental and leaves the pooled collections empty.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenManifestRefused_ReturnsEveryRental()
        {
            TestRootScope host = ManifestTestKit.AddInactiveHost(_bed, "Host");
            ManifestTestKit.AddNeverGenerated(host.gameObject);
            LogAssert.Expect(LogType.Exception, new Regex("InjectionManifestException"));
            int rentals = ComponentDiscovery.CountOutstandingRentals();

            host.gameObject.SetActive(true);

            Assert.AreEqual(rentals, ComponentDiscovery.CountOutstandingRentals(), "Every rental is returned");
            Assert.AreEqual(0, ComponentDiscovery.CountPooledReferences(), "Every pooled collection is empty");
            LogAssert.NoUnexpectedReceived();
            yield return null;
        }

        /// <summary>
        /// An explicit host's manifest refusal returns every discovery rental.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenManifestRefused_ReturnsEveryRental()
        {
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            ManifestTestKit.AddNeverGenerated(host.gameObject);
            host.Attach(ManifestTestKit.CreateRegion(_bed), ScopeOwnership.Owned);
            int rentals = ComponentDiscovery.CountOutstandingRentals();

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.IsTrue(prepare.IsFaulted, "The prepare faults");
            Assert.IsInstanceOf<InjectionManifestException>(prepare.Exception.InnerException, "The manifest is refused");
            Assert.AreEqual(rentals, ComponentDiscovery.CountOutstandingRentals(), "Every rental is returned");
        }

        /// <summary>
        /// <c>InjectGameObject</c>'s manifest refusal returns every discovery rental.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenManifestRefused_ReturnsEveryRental()
        {
            GameObject root = _bed.Create("R", null);
            root.AddComponent<InjectableMonoBehaviour>();
            ManifestTestKit.AddNeverGenerated(root);
            CountingResolver r = new CountingResolver();
            int rentals = ComponentDiscovery.CountOutstandingRentals();

            Assert.Throws<InjectionManifestException>(() => r.InjectGameObject(root));

            Assert.AreEqual(rentals, ComponentDiscovery.CountOutstandingRentals(), "Every rental is returned");
            yield return null;
        }

        /// <summary>
        /// <c>InitializeGameObject</c>'s manifest refusal returns every discovery rental.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InitializeGameObject_WhenManifestRefused_ReturnsEveryRental()
        {
            GameObject root = _bed.Create("R", null);
            root.AddComponent<CountingInitializableMonoBehaviour>();
            ManifestTestKit.AddNeverGenerated(root);
            IObjectResolver region = ManifestTestKit.CreateRegion(_bed);
            region.RunInitializables();
            int rentals = ComponentDiscovery.CountOutstandingRentals();

            Assert.Throws<InjectionManifestException>(() => region.InitializeGameObject(root));

            Assert.AreEqual(rentals, ComponentDiscovery.CountOutstandingRentals(), "Every rental is returned");
            yield return null;
        }

        /// <summary>
        /// A prepare from a valid manifest returns every rental, leaves the pooled collections empty and walks nothing.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenManifestUsed_LeavesPooledCollectionsEmpty()
        {
            _bed.Add<BoundCountingComponent>("A", null);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            ManifestTestKit.AddGenerated(host.gameObject, ManifestCoverage.Scene);
            host.Attach(ManifestTestKit.CreateRegion(_bed), ScopeOwnership.Owned);
            int rentals = ComponentDiscovery.CountOutstandingRentals();
            int walks = ComponentDiscovery.CountWalks();

            Task prepare = host.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.AreEqual(TaskStatus.RanToCompletion, prepare.Status, "The prepare ran to completion");
            Assert.AreEqual(rentals, ComponentDiscovery.CountOutstandingRentals(), "Every rental is returned");
            Assert.AreEqual(0, ComponentDiscovery.CountPooledReferences(), "Every pooled collection is empty");
            Assert.AreEqual(walks, ComponentDiscovery.CountWalks(), "Nothing is walked");
        }

        /// <summary>
        /// A given root destroyed before the call is skipped before its manifest is looked up, so the live root
        /// prepares from its own manifest without walking.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenGivenRootDestroyedBeforeCall_SkipsItsManifest()
        {
            GameObject r1 = _bed.Create("R1", null);
            r1.AddComponent<BoundCountingComponent>();
            GameObject r2 = _bed.Create("R2", null);
            BoundCountingComponent b = r2.AddComponent<BoundCountingComponent>();
            ManifestTestKit.AddGenerated(r1, ManifestCoverage.Hierarchy);
            ManifestTestKit.AddGenerated(r2, ManifestCoverage.Hierarchy);
            ExplicitScopeHost host = _bed.Add<ExplicitScopeHost>("Host", null);
            host.Attach(ManifestTestKit.CreateRegion(_bed), ScopeOwnership.Owned);
            GameObject[] roots = { r1, r2 };
            Object.DestroyImmediate(r1);
            int walks = ComponentDiscovery.CountWalks();

            Task prepare = host.PrepareAsync(roots, CancellationToken.None);
            yield return SceneTestBed.WaitFor(prepare);

            Assert.AreEqual(TaskStatus.RanToCompletion, prepare.Status, "The prepare ran to completion");
            Assert.IsNotNull(b.Service, "The live root's component is bound");
            Assert.AreEqual(1, b.InitializeCount, "The live root's component is initialized once");
            Assert.AreEqual(walks, ComponentDiscovery.CountWalks(), "Nothing is walked");
        }

        /// <summary>
        /// Builds the line an unusable manifest yields: the manifest's object, its problems and the fix.
        /// </summary>
        /// <param name="objectPath">The manifest's GameObject as <c>{scene}/{path}</c>.</param>
        /// <param name="problems">The manifest's problems, joined with <c>"; "</c>.</param>
        /// <returns>The refusal line for one manifest.</returns>
        private string Unusable(string objectPath, string problems)
        {
            return $"The InjectionManifest on {objectPath} cannot be used: {problems}. Rebuild the injection manifest, or remove the InjectionManifest component so the hierarchy is walked.";
        }

        /// <summary>
        /// Asserts that a refused prepare changed nothing: the host is still attached and its scope ran no pass.
        /// </summary>
        /// <param name="host">The explicit host whose prepare was refused.</param>
        /// <param name="region">The resolver attached to <paramref name="host"/>.</param>
        private void AssertUnchanged(ExplicitScopeHost host, IObjectResolver region)
        {
            Assert.AreEqual(ScopeHostState.Attached, host.State, "The host stays attached");
            Assert.AreEqual(LifecycleState.Created, region.GetLifecycleState(), "No pass ran");
        }
    }
}
