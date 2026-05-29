using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Verifies the <see cref="IInitializable"/> contract:
    /// <list type="bullet">
    ///     <item><c>RunInitializables</c> calls <see cref="IInitializable.Initialize"/> exactly once per instance, even when invoked multiple times</item>
    ///     <item>By the time <c>Initialize</c> runs (Phase 2), every Phase-1 binding (constructor injection) is complete; peers are observable</item>
    /// </list>
    ///
    /// Exercises <c>Scope.RunInitializables</c> and the <c>_initialized</c> tracking set.
    /// </summary>
    [TestFixture]
    public sealed class InitializableTests
    {
        /// <summary>
        /// <c>RunInitializables</c> must be idempotent; calling it twice on the same resolver
        /// must not re-run <c>Initialize</c> on already-initialized instances.
        /// Verifies the <c>HashSet&lt;object&gt; _initialized</c> deduplication path.
        /// </summary>
        [Test]
        public void RunInitializables_WhenInstanceImplementsIInitializable_CallsInitializeOnce()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<InitTracking>(Lifetime.Singleton));
            InitTracking instance = resolver.Resolve<InitTracking>();

            // Call twice; second call must be a no-op for already-initialized instances
            resolver.RunInitializables();
            resolver.RunInitializables();

            Assert.That(instance.InitializeCallCount, Is.EqualTo(1), "Initialize should only run once");
        }

        /// <summary>
        /// Verifies the Phase 1 (bindings) -> Phase 2 (Initialize) ordering: when
        /// <c>CrossInstanceInit</c>'s <c>Initialize</c> runs, its peer dependency (<c>InitTracking</c>) must already be constructed and
        /// bound. The peer's non-null reference inside <c>Initialize</c> proves Phase 1 completed first.
        /// </summary>
        [Test]
        public void RunInitializables_WhenInstanceDependsOnAnother_BothBoundBeforeEitherInitializes()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<InitTracking>(Lifetime.Singleton);
                b.Register<CrossInstanceInit>(Lifetime.Singleton);
            });

            // Resolve both; this constructs both (Phase 1) and parls them in the singleton cache.
            // RunInitializables then drives Phase 2 across both.
            InitTracking a = resolver.Resolve<InitTracking>();
            CrossInstanceInit b = resolver.Resolve<CrossInstanceInit>();

            resolver.RunInitializables();

            Assert.IsTrue(b.OtherWasReady, "Phase 2 sees Phase 1 bindings complete across the scope.");
        }
    }
}