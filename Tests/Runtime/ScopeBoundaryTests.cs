using System.Collections;
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
    /// Pins the <see cref="ScopeBoundary"/> rules:
    /// <list type="bullet">
    ///     <item>A walk skips a GameObject carrying a boundary, with all its descendants, unless that GameObject is a root passed to the call</item>
    ///     <item>A scene batch skips a boundary on a scene root too</item>
    ///     <item>A walk that claims a boundary root still skips the boundaries beneath it</item>
    ///     <item>An inactive boundary object and a disabled marker still count</item>
    ///     <item>A boundary subtree given to a child scope's explicit host is bound and initialized by that scope, the rest of the scene by the parent's</item>
    /// </list>
    /// Every scene and GameObject is runtime-created; tasks are driven by yielding frames until they complete or 10 s
    /// of real time pass.
    /// </summary>
    /// <remarks>
    /// Exercises <see cref="ScopeBoundary"/> through <see cref="LifetimeScopeBehaviour"/>,
    /// <see cref="ExplicitScopeHost"/> and <see cref="UnityResolverExtensions.InjectGameObject"/>.
    /// </remarks>
    [TestFixture]
    public sealed class ScopeBoundaryTests
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
            yield return _bed.SetUp("Boundary");
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
        /// A boundary beneath a scene root keeps its subtree out of the automatic host's walk: the inside component
        /// is neither bound nor initialized, the outside one is both.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenSubtreeHasScopeBoundary_LeavesItUnboundAndUninitialized()
        {
            BoundCountingComponent outside = _bed.Add<BoundCountingComponent>("Outside", null);
            GameObject content = _bed.Create("Content", null);
            GameObject boundary = CreateBoundary(content);
            BoundCountingComponent inside = _bed.Add<BoundCountingComponent>("Inside", boundary);

            _bed.Add<TestRootScope>("Host", null);

            Assert.IsNotNull(outside.Service, "The outside component is bound");
            Assert.AreEqual(1, outside.InitializeCount, "The outside component is initialized once");
            Assert.IsNull(inside.Service, "The inside component is not bound");
            Assert.AreEqual(0, inside.InitializeCount, "The inside component is not initialized");
            yield return null;
        }

        /// <summary>
        /// A boundary on a scene root is skipped by the scene batch, its own components included.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenSceneRootCarriesScopeBoundary_LeavesItUnbound()
        {
            GameObject boundary = CreateBoundary(null);
            BoundCountingComponent onBoundary = boundary.AddComponent<BoundCountingComponent>();
            BoundCountingComponent inside = _bed.Add<BoundCountingComponent>("Inside", boundary);

            _bed.Add<TestRootScope>("Host", null);

            Assert.IsNull(onBoundary.Service, "The boundary's own component is not bound");
            Assert.AreEqual(0, onBoundary.InitializeCount, "The boundary's own component is not initialized");
            Assert.IsNull(inside.Service, "The inside component is not bound");
            Assert.AreEqual(0, inside.InitializeCount, "The inside component is not initialized");
            yield return null;
        }

        /// <summary>
        /// A boundary root passed to <c>InjectGameObject</c> is claimed by the call, so its subtree is bound.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenGivenRootCarriesScopeBoundary_BindsItsSubtree()
        {
            GameObject boundary = CreateBoundary(null);
            InjectableMonoBehaviour onBoundary = boundary.AddComponent<InjectableMonoBehaviour>();
            InjectableMonoBehaviour inside = _bed.Add<InjectableMonoBehaviour>("Inside", boundary);
            IObjectResolver region = _bed.Track(Container.Build(b => b.Register<IPlayModeService, PlayModeService>(Lifetime.Singleton)));

            region.InjectGameObject(boundary);

            Assert.IsNotNull(onBoundary.Service, "The given boundary root's own component is bound");
            Assert.IsNotNull(inside.Service, "The given boundary root's subtree is bound");
            yield return null;
        }

        /// <summary>
        /// A walk over a given root still skips a boundary nested beneath it, with that boundary's descendants.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator InjectGameObject_WhenBoundaryNestedBeneathGivenRoot_SkipsNestedSubtree()
        {
            GameObject root = _bed.Create("R", null);
            InjectableMonoBehaviour a = root.AddComponent<InjectableMonoBehaviour>();
            GameObject nested = _bed.Create("Nested", root);
            nested.AddComponent<ScopeBoundary>();
            InjectableMonoBehaviour b = nested.AddComponent<InjectableMonoBehaviour>();
            InjectableMonoBehaviour c = _bed.Add<InjectableMonoBehaviour>("Grandchild", nested);
            IObjectResolver region = _bed.Track(Container.Build(builder => builder.Register<IPlayModeService, PlayModeService>(Lifetime.Singleton)));

            region.InjectGameObject(root);

            Assert.IsNotNull(a.Service, "The given root's own component is bound");
            Assert.IsNull(b.Service, "The nested boundary's own component is skipped");
            Assert.IsNull(c.Service, "The nested boundary's descendant is skipped");
            yield return null;
        }

        /// <summary>
        /// A boundary on an inactive GameObject still keeps its subtree out of the automatic host's walk.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenBoundaryObjectInactive_StillExcludesItsSubtree()
        {
            BoundCountingComponent outside = _bed.Add<BoundCountingComponent>("Outside", null);
            GameObject content = _bed.Create("Content", null);
            GameObject boundary = CreateBoundary(content);
            BoundCountingComponent inside = _bed.Add<BoundCountingComponent>("Inside", boundary);
            boundary.SetActive(false);

            _bed.Add<TestRootScope>("Host", null);

            Assert.IsNull(inside.Service, "The inside component is not bound");
            Assert.AreEqual(0, inside.InitializeCount, "The inside component is not initialized");
            Assert.IsNotNull(outside.Service, "The outside component is bound");
            yield return null;
        }

        /// <summary>
        /// A disabled boundary marker still keeps its subtree out of the automatic host's walk.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator Awake_WhenBoundaryComponentDisabled_StillExcludesItsSubtree()
        {
            BoundCountingComponent outside = _bed.Add<BoundCountingComponent>("Outside", null);
            GameObject content = _bed.Create("Content", null);
            GameObject boundary = CreateBoundary(content);
            BoundCountingComponent inside = _bed.Add<BoundCountingComponent>("Inside", boundary);
            boundary.GetComponent<ScopeBoundary>().enabled = false;

            _bed.Add<TestRootScope>("Host", null);

            Assert.IsNull(inside.Service, "The inside component is not bound");
            Assert.AreEqual(0, inside.InitializeCount, "The inside component is not initialized");
            Assert.IsNotNull(outside.Service, "The outside component is bound");
            yield return null;
        }

        /// <summary>
        /// A parent scope's explicit host prepares the scene minus a boundary subtree; a child scope's explicit host
        /// inside that subtree, given the boundary root, binds and initializes the inside component with its own
        /// scope. Each component is bound and initialized once, by its own scope, and both hosts become ready.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator PrepareAsync_WhenBoundarySubtreeGivenToChildHost_EachScopeBindsItsOwnComponents()
        {
            PlayModeService parentService = new PlayModeService();
            PlayModeService childService = new PlayModeService();
            IObjectResolver parent = _bed.Track(Container.Build(b => b.RegisterInstance<IPlayModeService>(parentService)));
            BoundCountingComponent outside = _bed.Add<BoundCountingComponent>("Outside", null);
            ExplicitScopeHost parentHost = _bed.Add<ExplicitScopeHost>("ParentHost", null);
            GameObject boundary = CreateBoundary(null);
            BoundCountingComponent inside = _bed.Add<BoundCountingComponent>("Inside", boundary);
            ExplicitScopeHost childHost = _bed.Add<ExplicitScopeHost>("ChildHost", boundary);
            parentHost.Attach(parent, ScopeOwnership.Owned);
            Task parentPrepare = parentHost.PrepareAsync(CancellationToken.None);
            yield return SceneTestBed.WaitFor(parentPrepare);
            IObjectResolver child = _bed.Track(parent.CreateChildScope(b => b.RegisterInstance<IPlayModeService>(childService)));
            childHost.Attach(child, ScopeOwnership.Owned);

            Task childPrepare = childHost.PrepareAsync(new[] { boundary }, CancellationToken.None);
            yield return SceneTestBed.WaitFor(childPrepare);

            Assert.AreEqual(TaskStatus.RanToCompletion, parentPrepare.Status, "The parent's prepare ran to completion");
            Assert.AreEqual(TaskStatus.RanToCompletion, childPrepare.Status, "The child's prepare ran to completion");
            Assert.AreSame(parentService, outside.Service, "The outside component is bound by the parent scope");
            Assert.AreEqual(1, outside.InitializeCount, "The outside component is initialized once");
            Assert.AreSame(childService, inside.Service, "The inside component is bound by the child scope");
            Assert.AreEqual(1, inside.InitializeCount, "The inside component is initialized once");
            Assert.AreEqual(ScopeHostState.Ready, parentHost.State, "The parent host is ready");
            Assert.AreEqual(ScopeHostState.Ready, childHost.State, "The child host is ready");
        }

        /// <summary>
        /// Creates a tracked GameObject named <c>Boundary</c> carrying a <see cref="ScopeBoundary"/>.
        /// </summary>
        /// <param name="parent">The parent GameObject; null for a scene root.</param>
        /// <returns>The boundary's GameObject.</returns>
        private GameObject CreateBoundary(GameObject parent)
        {
            GameObject boundary = _bed.Create("Boundary", parent);
            boundary.AddComponent<ScopeBoundary>();
            return boundary;
        }
    }
}
