using System;
using System.Collections.Generic;
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
}