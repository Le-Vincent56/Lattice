using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;

namespace Didionysymus.Lattice.Tests.Editor.Fixtures
{
    /// <summary>
    /// Shared ordered log of disposals for cleanup tests. Fixtures append <c>"name:dispose"</c> from their
    /// <c>Dispose</c>; tests clear <see cref="Events"/> in <c>[SetUp]</c>.
    /// </summary>
    public static class DisposalLog
    {
        public static readonly List<string> Events = new List<string>();
    }

    /// <summary>
    /// <see cref="IDisposable"/> that logs <c>"name:dispose"</c> to <see cref="DisposalLog"/> and counts its disposals,
    /// so a test can see the order of disposal and that nothing is disposed twice.
    /// </summary>
    public sealed class LoggedDisposable : IDisposable
    {
        private readonly string _name;
        public int DisposeCount { get; private set; }
        public LoggedDisposable(string name) => _name = name;

        public void Dispose()
        {
            DisposeCount++;
            DisposalLog.Events.Add(_name + ":dispose");
        }
    }

    /// <summary>
    /// <see cref="IDisposable"/> that logs <c>"name:dispose"</c> to <see cref="DisposalLog"/>, then throws
    /// <see cref="NotSupportedException"/> with its name as the message, a type no container refusal uses.
    /// </summary>
    public sealed class ThrowingDisposable : IDisposable
    {
        private readonly string _name;
        public ThrowingDisposable(string name) => _name = name;

        public void Dispose()
        {
            DisposalLog.Events.Add(_name + ":dispose");
            throw new NotSupportedException(_name);
        }
    }

    /// <summary>
    /// <see cref="IDisposable"/> that records, when it is disposed, whether the token a test handed it was already
    /// cancelled, so a test can see that cancellation came before disposal.
    /// </summary>
    public sealed class TokenObservingDisposable : IDisposable
    {
        public CancellationToken Token { get; set; }
        public bool TokenCanceledAtDispose { get; private set; }
        public void Dispose() => TokenCanceledAtDispose = Token.IsCancellationRequested;
    }

    /// <summary>
    /// Disposable Transient whose constructor signals <see cref="Entered"/> and then waits on <see cref="Gate"/>, so a
    /// test can dispose the scope while a resolve is building it. The wait is bounded, so a test that never opens the
    /// gate fails instead of hanging. Tests reset both events in <c>[SetUp]</c>; <see cref="LastCreated"/> holds the
    /// most recent instance.
    /// </summary>
    public sealed class GatedDisposableTransient : IDisposable
    {
        public static readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
        public static readonly ManualResetEventSlim Gate = new ManualResetEventSlim(false);
        public static GatedDisposableTransient LastCreated;
        public bool IsDisposed { get; private set; }

        public GatedDisposableTransient()
        {
            LastCreated = this;
            Entered.Set();
            Gate.Wait(5000);
        }

        public void Dispose() => IsDisposed = true;
    }

    /// <summary>
    /// <see cref="IInitializable"/> whose <c>Initialize</c> disposes <see cref="Target"/>, standing for an
    /// initializer that tears down its own scope. Tests set <see cref="Target"/> before the pass.
    /// </summary>
    public sealed class ScopeDisposingInitializable : IInitializable
    {
        public static IObjectResolver Target;
        public void Initialize() => Target.Dispose();
    }

    /// <summary>
    /// <see cref="IDisposable"/> whose <c>Dispose</c> disposes <see cref="Target"/>, its own scope, once, then logs
    /// <c>reentrant:dispose</c>. Tests set <see cref="Target"/> before disposing.
    /// </summary>
    public sealed class ReentrantDisposable : IDisposable
    {
        public static IObjectResolver Target;
        private bool _disposing;

        public void Dispose()
        {
            if (!_disposing)
            {
                _disposing = true;
                Target.Dispose();
            }

            DisposalLog.Events.Add("reentrant:dispose");
        }
    }

    /// <summary>
    /// <see cref="IInitializable"/> that is also <see cref="IDisposable"/> and logs <c>"name:dispose"</c> to
    /// <see cref="DisposalLog"/>, so a test can see that a participant built by a failed pass is still disposed.
    /// </summary>
    public sealed class DisposableInitializable : IInitializable, IDisposable
    {
        private readonly string _name;
        public DisposableInitializable(string name) => _name = name;

        public void Initialize()
        {
        }

        public void Dispose() => DisposalLog.Events.Add(_name + ":dispose");
    }

    /// <summary>
    /// <see cref="IAsyncStartable"/> that is also <see cref="IDisposable"/> and logs <c>"name:dispose"</c> to
    /// <see cref="DisposalLog"/>, so a test can see that a participant of a failed startup is still disposed.
    /// </summary>
    public sealed class DisposableStartable : IAsyncStartable, IDisposable
    {
        private readonly string _name;
        public DisposableStartable(string name) => _name = name;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => DisposalLog.Events.Add(_name + ":dispose");
    }

    /// <summary>
    /// <see cref="IAsyncStartable"/> that keeps the token it received and completes at once, standing for a
    /// participant that holds its token for background work after startup.
    /// </summary>
    public sealed class TokenKeepingStartable : IAsyncStartable
    {
        public CancellationToken ReceivedToken { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            ReceivedToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// <see cref="IAsyncStartable"/> that calls <c>ShutdownAsync</c> on <see cref="Target"/>, its own scope, from
    /// inside its first <c>StartAsync</c> and keeps the returned task in <see cref="ReturnedTask"/> without awaiting
    /// it. Calls only once. Tests set <see cref="Target"/> before startup.
    /// </summary>
    public sealed class ShutdownCallingStartable : IAsyncStartable
    {
        public static IObjectResolver Target;
        public Task ReturnedTask { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (ReturnedTask == null) ReturnedTask = Target.ShutdownAsync(CancellationToken.None);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// <see cref="IAsyncStartable"/> that registers a callback on its token which disposes <see cref="Target"/>, its
    /// own scope, then completes only when a test completes <see cref="Gate"/>. Tests set <see cref="Target"/> before
    /// startup.
    /// </summary>
    public sealed class DisposingCallbackStartable : IAsyncStartable
    {
        public static IObjectResolver Target;
        public TaskCompletionSource<bool> Gate { get; } = new TaskCompletionSource<bool>();

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => Target.Dispose());
            return Gate.Task;
        }
    }

    /// <summary>
    /// <see cref="IDisposable"/> that records the synchronization context and managed thread its <c>Dispose</c> ran
    /// on.
    /// </summary>
    public sealed class ContextRecordingDisposable : IDisposable
    {
        public SynchronizationContext RecordedContext { get; private set; }
        public int RecordedThreadID { get; private set; }

        public void Dispose()
        {
            RecordedContext = SynchronizationContext.Current;
            RecordedThreadID = Environment.CurrentManagedThreadId;
        }
    }
}
