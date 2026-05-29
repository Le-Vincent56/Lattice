using System.Collections.Concurrent;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Concurrency
{
    /// <summary>
    /// Verifies that resolution is thread-safe.
    /// <list type="bullet">
    ///     <item>
    ///         Singleton resolves under concurrent contention return the same reference;
    ///         the root scope <c>_lock</c> must wrap check and create and cache atomically.
    ///     </item>
    ///     <item>
    ///         Transient resolves under contention do not raise; the per-resolve allocation
    ///         path has no shared mutable state to corrupt.
    ///     </item>
    /// </list>
    ///
    /// Uses <see cref="Parallel.For"/> to drive 64-way contention.
    /// Catches double-construction races in <c>Scope.MaterializeFromEntry</c>'s Singleton
    /// branch.
    /// </summary>
    [TestFixture]
    public sealed class ResolutionConcurrencyTests
    {
        // The resolver lives as an instance field (rather than a using-local) so that
        // Parallel.For's lambda captures 'this.-resolver' instead of a local.
        // Field capture sidesteps the AccessToDisposedClosure analyzer warning; the analyzer can't see that Parallel.For
        // is synchronous, so any local + dispose pattern triggers a false-positive "captured variable is disposed in the outer scope".
        // [TearDown] handles disposal cleanly between tests.
        private IObjectResolver _resolver;

        [TearDown]
        public void TearDown()
        {
            _resolver?.Dispose();
            _resolver = null;
        }

        /// <summary>
        /// Concurrent Singleton resolution must yield the same instance for all threads.
        /// A race in the Singleton branch's cache check would manifest as multiple <c>ServiceA</c>
        /// instances landing in the bag with distinct InstanceIDs.
        /// </summary>
        [Test]
        public void Resolve_WhenSingletonResolvedConcurrently_ReturnsSameInstance()
        {
            _resolver = Container.Build(b => b.Register<IServiceA, ServiceA>(Lifetime.Singleton));
            ConcurrentBag<IServiceA> bag = new ConcurrentBag<IServiceA>();

            // 64 parallel resolves. The thread pool typically runs many of these in parallel,
            // so any race in the Singleton cache will surface here.
            Parallel.For(0, 64, i => { bag.Add(_resolver.Resolve<IServiceA>()); });

            // Walk the bag once; first element is the seed for SameAs comparison.
            IServiceA first = null;
            foreach (IServiceA item in bag)
            {
                if (first == null)
                {
                    first = item;
                    continue;
                }

                Assert.AreSame(
                    item,
                    first,
                    "Concurrent Singleton resolves must return the same reference (lock-protected cache)."
                );
            }

            Assert.IsNotNull(first, "Bag must contain at least one resolution.");
        }

        /// <summary>
        /// Concurrent Transient resolution must not throw. There's no caching to race on,
        /// but other shared state (the per-thread resolution stack, the disposables list) could still
        /// misbehave. This test catches such regressions broadly.
        /// </summary>
        [Test]
        public void Resolve_WhenTransientResolvedConcurrently_DoesNotThrow()
        {
            _resolver = Container.Build(b => b.Register<IServiceA, ServiceA>(Lifetime.Transient));

            Assert.DoesNotThrow(() =>
            {
                Parallel.For(0, 64, i =>
                {
                    // Discard the resolved instance; we only care that resolution doesn't throw.
                    _ = _resolver.Resolve<IServiceA>();
                });
            });
        }
    }
}