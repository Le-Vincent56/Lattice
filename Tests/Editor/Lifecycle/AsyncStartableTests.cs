using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Verifies the <see cref="IAsyncStartable"/> contract:
    /// <list type="bullet">
    ///     <item>
    ///         <c>RunAsyncStartablesAsync</c> awaits each registration's <c>StartAsync</c> sequentially in registration order;
    ///         the next does not enter until the previous has exited
    ///     </item>
    /// </list>
    ///
    /// Exercises <c>Scope.RunAsyncStartablesAsync</c> and its registry walk.
    /// </summary>
    [TestFixture]
    public sealed class AsyncStartableTests
    {
        /// <summary>
        /// Resets the shared <see cref="AsyncStartLogger.Order"/> list and <see cref="ValueEqualStartable.StartCount"/>
        /// between tests. Both are static, so without this reset, entries leak across test runs.
        /// </summary>
        [SetUp]
        public void Reset()
        {
            AsyncStartLogger.Order.Clear();
            ValueEqualStartable.StartCount = 0;
        }

        /// <summary>
        /// Multiple <see cref="IAsyncStartable"/> registrations must run sequentially; each one's
        /// <c>StartAsync</c> is fully awaited before the next is invoked. The <c>Task.Yield()</c> inside
        /// <see cref="AsyncStartLogger.StartAsync"/> would interleave enter/exit entries if the runner accidentally ran
        /// them in parallel.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenMultipleRegistered_RunsInRegistrationOrder()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => new AsyncStartLogger("A"), Lifetime.Singleton);
                b.RegisterFactory<IAsyncStartable>(_ => new AsyncStartLogger("B"), Lifetime.Singleton);
                b.RegisterFactory<IAsyncStartable>(_ => new AsyncStartLogger("C"), Lifetime.Singleton);
            });

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            // Sequential: each enter is followed by its own exit before the next enter.
            // Any parallelism would produce interleaved entries (e.g., A:enter, B:enter, ...).
            Assert.That(
                AsyncStartLogger.Order,
                Is.EqualTo(new[]
                {
                    "A:enter", "A:exit",
                    "B:enter", "B:exit",
                    "C:enter", "C:exit"
                })
            );
        }

        /// <summary>
        /// Two registrations whose instances are equal by <c>Equals</c> are still two objects, and each is started.
        /// Guards the once-per-call rule against comparing participants by value instead of by reference.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenDistinctStartablesAreEqual_StartsBoth()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => new ValueEqualStartable(), Lifetime.Singleton);
                b.RegisterFactory<IAsyncStartable>(_ => new ValueEqualStartable(), Lifetime.Singleton);
            });

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            Assert.AreEqual(2, ValueEqualStartable.StartCount, "Distinct instances are started even when they compare equal");
        }
    }
}