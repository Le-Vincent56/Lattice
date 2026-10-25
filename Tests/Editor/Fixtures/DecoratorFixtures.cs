using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;

namespace Didionysymus.Lattice.Tests.Editor.Fixtures
{
    /// <summary>
    /// Test fixtures for decorator contract tests. The decorators forward <see cref="Log"/>
    /// to <c>_inner.Log</c> all the way down to see <<completionlist cref="RealLoggable"/>, which owns the
    /// actual <c>List&lt;string&gt;</c>. Decorators append by casting the inner's
    /// <see cref="IReadOnlyList{T}"/> back to <c>List&lt;string&gt;</c>; fragile but intentional:
    /// it lets all decorators write to a single physical list so test assertions can read the full chain
    /// in one place.
    /// </summary>
    public interface ILoggable
    {
        IReadOnlyList<string> Log { get; }
        void Emit(string message);
    }

    /// <summary>
    /// The innermost real implementation. Owns the underlying <c>List&lt;string&gt;</c>
    /// that decorators ultimately append to.
    /// </summary>
    public sealed class RealLoggable : ILoggable
    {
        private readonly List<string> _log = new List<string>();
        public IReadOnlyList<string> Log => _log;
        public void Emit(string message) => _log.Add($"real:{message}");
    }

    /// <summary>
    /// First decorator in the chain (innermost wrap). Forwards then appends "first:".
    /// </summary>
    public sealed class FirstDecorator : ILoggable
    {
        private readonly ILoggable _inner;
        public FirstDecorator(ILoggable inner) => _inner = inner;
        public IReadOnlyList<string> Log => _inner.Log;

        public void Emit(string message)
        {
            _inner.Emit(message);
            ((List<string>)_inner.Log).Add($"first:{message}");
        }
    }

    /// <summary>
    /// Second decorator in the chain (outermost wrap). Forwards then appends "second:".
    /// </summary>
    public sealed class SecondDecorator : ILoggable
    {
        private readonly ILoggable _inner;
        public SecondDecorator(ILoggable inner) => _inner = inner;
        public IReadOnlyList<string> Log => _inner.Log;

        public void Emit(string message)
        {
            _inner.Emit(message);
            ((List<string>)_inner.Log).Add($"second:{message}");
        }
    }

    /// <summary>
    /// <see cref="ILoggable"/> that records whether the container disposed and initialized it. Used as the inner
    /// object of a decorated registration: its flags show the container reached the inner object, not only the wrapper.
    /// </summary>
    public sealed class TrackedLoggable : ILoggable, IDisposable, IInitializable
    {
        private readonly List<string> _log = new List<string>();
        public IReadOnlyList<string> Log => _log;
        public bool IsDisposed { get; private set; }
        public int InitializeCount { get; private set; }
        public void Emit(string message) => _log.Add(message);
        public void Dispose() => IsDisposed = true;
        public void Initialize() => InitializeCount++;
    }

    /// <summary>
    /// Decorator that exposes the object it wraps as <see cref="Inner"/> and records its own disposal and
    /// initialization. It does not forward <c>Dispose</c> or <c>Initialize</c>, so the inner object's flags change
    /// only when the container reaches the inner object directly.
    /// </summary>
    public sealed class TrackedLoggableDecorator : ILoggable, IDisposable, IInitializable
    {
        public ILoggable Inner { get; }
        public IReadOnlyList<string> Log => Inner.Log;
        public bool IsDisposed { get; private set; }
        public int InitializeCount { get; private set; }
        public TrackedLoggableDecorator(ILoggable inner) => Inner = inner;
        public void Emit(string message) => Inner.Emit(message);
        public void Dispose() => IsDisposed = true;
        public void Initialize() => InitializeCount++;
    }

    /// <summary>
    /// Service interface for decorator-chain ownership tests. Implementations and decorators carry no behavior of
    /// their own beyond logging their lifecycle hooks.
    /// </summary>
    public interface IChained
    {
    }

    /// <summary>
    /// Innermost <see cref="IChained"/> that takes part in every hook: logs <c>leaf:init</c> and <c>leaf:start</c> to
    /// <see cref="LifecycleEventLog"/> and <c>leaf:dispose</c> to <see cref="DisposalLog"/>.
    /// </summary>
    public sealed class ChainedLeaf : IChained, IInitializable, IAsyncStartable, IDisposable
    {
        public void Initialize() => LifecycleEventLog.Events.Add("leaf:init");

        public Task StartAsync(CancellationToken cancellationToken)
        {
            LifecycleEventLog.Events.Add("leaf:start");
            return Task.CompletedTask;
        }

        public void Dispose() => DisposalLog.Events.Add("leaf:dispose");
    }

    /// <summary>
    /// First registered <see cref="IChained"/> decorator, the middle link of a two-decorator chain. Logs
    /// <c>inner:init</c>, <c>inner:start</c> and <c>inner:dispose</c>, and forwards none of them to the object it
    /// wraps.
    /// </summary>
    public sealed class InnerChainDecorator : IChained, IInitializable, IAsyncStartable, IDisposable
    {
        public IChained Inner { get; }
        public InnerChainDecorator(IChained inner) => Inner = inner;
        public void Initialize() => LifecycleEventLog.Events.Add("inner:init");

        public Task StartAsync(CancellationToken cancellationToken)
        {
            LifecycleEventLog.Events.Add("inner:start");
            return Task.CompletedTask;
        }

        public void Dispose() => DisposalLog.Events.Add("inner:dispose");
    }

    /// <summary>
    /// Second registered <see cref="IChained"/> decorator, the outermost link. Logs <c>outer:init</c>,
    /// <c>outer:start</c> and <c>outer:dispose</c>, and forwards none of them to the object it wraps.
    /// </summary>
    public sealed class OuterChainDecorator : IChained, IInitializable, IAsyncStartable, IDisposable
    {
        public IChained Inner { get; }
        public OuterChainDecorator(IChained inner) => Inner = inner;
        public void Initialize() => LifecycleEventLog.Events.Add("outer:init");

        public Task StartAsync(CancellationToken cancellationToken)
        {
            LifecycleEventLog.Events.Add("outer:start");
            return Task.CompletedTask;
        }

        public void Dispose() => DisposalLog.Events.Add("outer:dispose");
    }

    /// <summary>
    /// Disposable <see cref="IChained"/> for Transient chains; logs <c>leaf:dispose</c> to <see cref="DisposalLog"/>.
    /// </summary>
    public sealed class DisposableTransientLeaf : IChained, IDisposable
    {
        public void Dispose() => DisposalLog.Events.Add("leaf:dispose");
    }

    /// <summary>
    /// <see cref="IChained"/> decorator whose constructor throws <see cref="NotSupportedException"/>, so the chain
    /// fails after the object it wraps was built.
    /// </summary>
    public sealed class ThrowingChainDecorator : IChained
    {
        public ThrowingChainDecorator(IChained inner) => throw new NotSupportedException("decorator failed");
    }
}