using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;

namespace Didionysymus.Lattice.Tests.Editor.Fixtures
{
    /// <summary>
    /// Test fixtures for lifecycle-hook tests (<see cref="IInitializable"/> and
    /// <see cref="IAsyncStartable"/>). All instances are designed to expose their
    /// hook-call observability via simple counters or a shared static list.
    /// </summary>
    internal static class LifecycleFixtureMarker
    {
    }

    /// <summary>
    /// <see cref="IInitializable"/> fixture that counts <see cref="Initialize"/>
    /// calls. Used to verify <c>RunInitializables</c> runs exactly once per instance
    /// regardless of how many times the user invokes it.
    /// </summary>
    public sealed class InitTracking : IInitializable
    {
        public int InitializeCallCount { get; private set; }
        public void Initialize() => InitializeCallCount++;
    }

    /// <summary>
    /// <see cref="IInitializable"/> fixture that takes a peer dependency. During
    /// <see cref="Initialize"/> (Phase 2), the peer's bindings (Phase 1) must be complete;
    /// the peer reference must be non-null. Verifies the Phase 1 / Phase 2 ordering contract.
    /// </summary>
    public sealed class CrossInstanceInit : IInitializable
    {
        public InitTracking Other { get; }
        public bool OtherWasReady { get; private set; }
        public CrossInstanceInit(InitTracking other) => Other = other;

        public void Initialize()
        {
            // By the time this runs (Phase 2), Other's construction (Phase 1) is complete.
            // Recording its non-null state is sufficient evidence; DI populated the field in
            // the constructor, so this is really verifying that Initialize ran AFTER
            // construction, not interleaved with it.
            OtherWasReady = Other != null;
        }
    }

    /// <summary>
    /// <see cref="IAsyncStartable"/> fixture that records "label:enter" / "label:exit"
    /// entries in a shared static <see cref="Order"/> list. Tests assert on this list to
    /// verify sequential await ordering across multiple registrations.
    /// </summary>
    /// <remarks>
    /// <see cref="Order"/> is shared across the text fixture lifetime. Tests must reset
    /// it in <c>[SetUp]</c> to avoid leaking entries across test methods.
    /// </remarks>
    public sealed class AsyncStartLogger : IAsyncStartable
    {
        private readonly string _label;
        public static readonly List<string> Order = new List<string>();
        public AsyncStartLogger(string label) => _label = label;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Order.Add($"{_label}:enter");

            // Yield forces the continuation onto a fresh turn; exposes any
            // accidental parallelism: if two startables run
            // concurrently, their enter/exit pairs would interleave instead of nesting
            await Task.Yield();
            Order.Add($"{_label}:exit");
        }
    }

    /// <summary>
    /// <see cref="IAsyncStartable"/> whose instances are all equal by <see cref="Equals(object)"/> and share one hash
    /// code while remaining distinct objects. Counts <see cref="StartAsync"/> calls across every instance in a static
    /// field; tests reset <see cref="StartCount"/> in <c>[SetUp]</c>.
    /// </summary>
    public sealed class ValueEqualStartable : IAsyncStartable
    {
        public static int StartCount;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Treats every instance of this type as equal, so only reference identity can tell two instances apart.
        /// </summary>
        /// <param name="obj">The object to compare with.</param>
        /// <returns>True when <paramref name="obj"/> is any <see cref="ValueEqualStartable"/>.</returns>
        public override bool Equals(object obj) => obj is ValueEqualStartable;

        /// <summary>
        /// Returns one hash code for every instance, consistent with <see cref="Equals(object)"/>.
        /// </summary>
        /// <returns>A constant.</returns>
        public override int GetHashCode() => 1;
    }
}