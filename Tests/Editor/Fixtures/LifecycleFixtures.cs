using System;
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

    /// <summary>
    /// Shared ordered log of lifecycle events for order tests. Fixtures append <c>"name:ctor"</c> from their
    /// constructors and <c>"name:init"</c> from <c>Initialize</c>; tests clear <see cref="Events"/> in <c>[SetUp]</c>.
    /// </summary>
    public static class LifecycleEventLog
    {
        public static readonly List<string> Events = new List<string>();
    }

    /// <summary>
    /// Dependency-free <see cref="IInitializable"/> that logs its construction and initialization as <c>leaf</c>.
    /// </summary>
    public sealed class LoggedLeaf : IInitializable
    {
        public LoggedLeaf() => LifecycleEventLog.Events.Add("leaf:ctor");
        public void Initialize() => LifecycleEventLog.Events.Add("leaf:init");
    }

    /// <summary>
    /// Dependency-free <see cref="IInitializable"/> that logs as <c>solo</c>. Registered beside
    /// <see cref="LoggedLeaf"/> to observe the order of independent participants.
    /// </summary>
    public sealed class LoggedSolo : IInitializable
    {
        public LoggedSolo() => LifecycleEventLog.Events.Add("solo:ctor");
        public void Initialize() => LifecycleEventLog.Events.Add("solo:init");
    }

    /// <summary>
    /// <see cref="IInitializable"/> that depends on <see cref="LoggedLeaf"/> and logs as <c>dependent</c>, so a test
    /// can see that the dependency is constructed and initialized first.
    /// </summary>
    public sealed class LoggedDependent : IInitializable
    {
        public LoggedLeaf Leaf { get; }

        public LoggedDependent(LoggedLeaf leaf)
        {
            Leaf = leaf;
            LifecycleEventLog.Events.Add("dependent:ctor");
        }

        public void Initialize() => LifecycleEventLog.Events.Add("dependent:init");
    }

    /// <summary>
    /// <see cref="ILoggable"/> implementation that logs its initialization as <c>inner</c>. Paired with
    /// <see cref="LoggedLoggableDecorator"/> to observe that a decorated instance initializes before its decorator.
    /// </summary>
    public sealed class LoggedLoggable : ILoggable, IInitializable
    {
        private readonly List<string> _log = new List<string>();
        public IReadOnlyList<string> Log => _log;
        public void Emit(string message) => _log.Add(message);
        public void Initialize() => LifecycleEventLog.Events.Add("inner:init");
    }

    /// <summary>
    /// <see cref="ILoggable"/> decorator that logs its initialization as <c>wrapper</c>. It does not forward
    /// <c>Initialize</c> to the instance it wraps; the container initializes the inner instance itself.
    /// </summary>
    public sealed class LoggedLoggableDecorator : ILoggable, IInitializable
    {
        private readonly ILoggable _inner;
        public LoggedLoggableDecorator(ILoggable inner) => _inner = inner;
        public IReadOnlyList<string> Log => _inner.Log;
        public void Emit(string message) => _inner.Emit(message);
        public void Initialize() => LifecycleEventLog.Events.Add("wrapper:init");
    }

    /// <summary>
    /// <see cref="IInitializable"/> whose instances are all equal by <see cref="Equals(object)"/> and share one hash
    /// code while remaining distinct objects. Counts <see cref="Initialize"/> calls across every instance in a static
    /// field; tests reset <see cref="InitializeCount"/> in <c>[SetUp]</c>.
    /// </summary>
    public sealed class ValueEqualInitializable : IInitializable
    {
        public static int InitializeCount;

        public void Initialize() => InitializeCount++;

        /// <summary>
        /// Treats every instance of this type as equal, so only reference identity can tell two instances apart.
        /// </summary>
        /// <param name="obj">The object to compare with.</param>
        /// <returns>True when <paramref name="obj"/> is any <see cref="ValueEqualInitializable"/>.</returns>
        public override bool Equals(object obj) => obj is ValueEqualInitializable;

        /// <summary>
        /// Returns one hash code for every instance, consistent with <see cref="Equals(object)"/>.
        /// </summary>
        /// <returns>A constant.</returns>
        public override int GetHashCode() => 1;
    }

    /// <summary>
    /// <see cref="IServiceA"/> that is also <see cref="IInitializable"/>. Registered through a factory under
    /// <see cref="IServiceA"/>, its hook is invisible to the pass; registered under its own type it is visible.
    /// </summary>
    public sealed class InitializableServiceA : IServiceA, IInitializable
    {
        public Guid InstanceID { get; } = Guid.NewGuid();
        public int InitializeCount { get; private set; }
        public void Initialize() => InitializeCount++;
    }

    /// <summary>
    /// <see cref="IInitializable"/> whose <see cref="Initialize"/> always throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public sealed class ThrowingInitializable : IInitializable
    {
        public void Initialize() => throw new InvalidOperationException("Initialize failed on purpose.");
    }

    /// <summary>
    /// <see cref="IInitializable"/> whose constructor needs <see cref="IServiceC"/>, which tests leave unregistered,
    /// so building it throws <c>RegistrationNotFoundException</c>. <c>Build</c> tolerates the missing dependency.
    /// </summary>
    public sealed class NeedsUnregisteredDependency : IInitializable
    {
        public IServiceC Dependency { get; }
        public NeedsUnregisteredDependency(IServiceC dependency) => Dependency = dependency;

        public void Initialize()
        {
        }
    }

    /// <summary>
    /// <see cref="IInitializable"/> that calls <c>RunInitializables</c> on its resolver from inside
    /// <see cref="Initialize"/> and stores what that call throws in <see cref="CaughtException"/>, so a test can
    /// assert the re-entrancy rule without faulting the scope.
    /// </summary>
    public sealed class ReentrantInitializable : IInitializable
    {
        private readonly IObjectResolver _resolver;
        public Exception CaughtException { get; private set; }
        public ReentrantInitializable(IObjectResolver resolver) => _resolver = resolver;

        public void Initialize()
        {
            try
            {
                _resolver.RunInitializables();
            }
            catch (Exception exception)
            {
                CaughtException = exception;
            }
        }
    }
}