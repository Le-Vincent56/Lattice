using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Unity;
using UnityEngine;

namespace Didionysymus.Lattice.Tests.Runtime.Fixtures
{
    /// <summary>
    /// Shared ordered log of preparation events for the explicit host tests. Fixtures append
    /// <c>"service:init"</c>, <c>"component:init"</c> and <c>"startable:start"</c>; tests clear <see cref="Events"/>
    /// in <c>[UnitySetUp]</c>.
    /// </summary>
    public static class HostEventLog
    {
        public static readonly List<string> Events = new List<string>();
    }

    /// <summary>
    /// Service a boot scope owns; a region scope's services depend on it, so a test can see that shutting a region
    /// down leaves the boot scope's instance alive.
    /// </summary>
    public interface IBootService
    {
        int DisposeCount { get; }
    }

    /// <summary>
    /// Concrete <see cref="IBootService"/> that counts <c>Dispose</c> calls.
    /// </summary>
    public sealed class BootService : IBootService, IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    /// <summary>
    /// Service a region scope owns. It depends on the boot scope's <see cref="IBootService"/>, reports whether its
    /// own <c>Initialize</c> has run, and counts <c>Dispose</c> calls.
    /// </summary>
    public interface IRegionService
    {
        IBootService Boot { get; }
        bool IsInitialized { get; }
        int DisposeCount { get; }
    }

    /// <summary>
    /// Concrete <see cref="IRegionService"/>; <c>Initialize</c> logs <c>service:init</c> and flips
    /// <see cref="IsInitialized"/>.
    /// </summary>
    public sealed class RegionService : IRegionService, IInitializable, IDisposable
    {
        public IBootService Boot { get; }
        public bool IsInitialized { get; private set; }
        public int DisposeCount { get; private set; }
        public RegionService(IBootService boot) => Boot = boot;

        public void Initialize()
        {
            HostEventLog.Events.Add("service:init");
            IsInitialized = true;
        }

        public void Dispose() => DisposeCount++;
    }

    /// <summary>
    /// <see cref="IAsyncStartable"/> that logs <c>startable:start</c>, counts its starts and completes at once.
    /// </summary>
    public sealed class LoggedStartable : IAsyncStartable
    {
        public int StartCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            HostEventLog.Events.Add("startable:start");
            StartCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// <see cref="IAsyncStartable"/> whose start completes only when a test completes <see cref="Gate"/>. It ignores
    /// its token, so it stands for a participant that does not cooperate with cancellation, and records the token it
    /// received and how often it was started.
    /// </summary>
    public sealed class PlayModeGatedStartable : IAsyncStartable
    {
        public TaskCompletionSource<bool> Gate { get; } = new TaskCompletionSource<bool>();
        public CancellationToken ReceivedToken { get; private set; }
        public int StartCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            ReceivedToken = cancellationToken;
            return Gate.Task;
        }
    }

    /// <summary>
    /// <see cref="IAsyncStartable"/> whose <c>StartAsync</c> throws <see cref="NotSupportedException"/> with the
    /// message <c>region start failed</c>, so the scope's startup fails.
    /// </summary>
    public sealed class ThrowingRegionStartable : IAsyncStartable
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new NotSupportedException("region start failed");
    }

    /// <summary>
    /// Scene component bound to an <see cref="IRegionService"/>. Counts its <c>Awake</c> and <c>Initialize</c> calls,
    /// records whether the service was bound when <c>Awake</c> ran, and whether it was bound and initialized when
    /// <c>Initialize</c> ran; <c>Initialize</c> logs <c>component:init</c>.
    /// </summary>
    public sealed class RegionComponent : MonoBehaviour, IInitializable
    {
        [Inject] public IRegionService Service;
        public int InitializeCount { get; private set; }
        public int AwakeCount { get; private set; }
        public bool ServiceWasNullInAwake { get; private set; }
        public bool ServiceInitializedAtInitialize { get; private set; }

        /// <summary>
        /// Counts the call and records whether <see cref="Service"/> was bound at that moment.
        /// </summary>
        private void Awake()
        {
            AwakeCount++;
            ServiceWasNullInAwake = Service == null;
        }

        public void Initialize()
        {
            HostEventLog.Events.Add("component:init");
            InitializeCount++;
            ServiceInitializedAtInitialize = Service != null && Service.IsInitialized;
        }
    }

    /// <summary>
    /// Service interface no test registers, so binding <see cref="MissingDependencyComponent"/> fails.
    /// </summary>
    public interface IMissingService
    {
    }

    /// <summary>
    /// Scene component whose <c>[Inject]</c> field needs the unregistered <see cref="IMissingService"/>, so binding
    /// it throws <c>RegistrationNotFoundException</c>.
    /// </summary>
    public sealed class MissingDependencyComponent : MonoBehaviour
    {
        [Inject] public IMissingService Missing;
    }

    /// <summary>
    /// <see cref="IAsyncStartable"/> that calls <see cref="ExplicitScopeHost.ShutdownAsync"/> and/or
    /// <see cref="ExplicitScopeHost.WaitUntilReadyAsync"/> on <see cref="Target"/> from inside its own
    /// <c>StartAsync</c>, as flagged, with <see cref="CancellationToken.None"/>, and stores the returned tasks without
    /// awaiting them; its own start completes at once.
    /// </summary>
    public sealed class HostCallingStartable : IAsyncStartable
    {
        public ExplicitScopeHost Target { get; set; }
        public bool CallShutdown { get; set; }
        public bool CallWaitUntilReady { get; set; }
        public Task ShutdownTask { get; private set; }
        public Task ReadyTask { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (CallShutdown) ShutdownTask = Target.ShutdownAsync(CancellationToken.None);
            if (CallWaitUntilReady) ReadyTask = Target.WaitUntilReadyAsync(CancellationToken.None);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// <see cref="IAsyncStartable"/> whose start waits for a test to complete <see cref="Gate"/>, then calls
    /// <see cref="ExplicitScopeHost.ShutdownAsync"/> on <see cref="Target"/> with <see cref="CancellationToken.None"/>
    /// and stores the returned task without awaiting it, so a test can call the host from inside its startup while a
    /// shutdown is already waiting for that startup.
    /// </summary>
    public sealed class GatedHostCallingStartable : IAsyncStartable
    {
        public ExplicitScopeHost Target { get; set; }
        public TaskCompletionSource<bool> Gate { get; } = new TaskCompletionSource<bool>();
        public Task ShutdownTask { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await Gate.Task;
            ShutdownTask = Target.ShutdownAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// <see cref="IAsyncStartable"/> that destroys <see cref="Target"/> with <c>Object.DestroyImmediate</c> from
    /// inside its own <c>StartAsync</c>, so a host on that GameObject is destroyed inside its own startup; its own
    /// start completes at once.
    /// </summary>
    public sealed class HostDestroyingStartable : IAsyncStartable
    {
        public GameObject Target { get; set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            UnityEngine.Object.DestroyImmediate(Target);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Scene component that starts <see cref="Target"/>'s shutdown from its own <c>Initialize</c>, that is, from the
    /// synchronous part of the host's preparation, and stores the returned task without awaiting it.
    /// </summary>
    public sealed class ShutdownCallingComponent : MonoBehaviour, IInitializable
    {
        public ExplicitScopeHost Target { get; set; }
        public Task ShutdownTask { get; private set; }

        public void Initialize() => ShutdownTask = Target.ShutdownAsync(CancellationToken.None);
    }
}
