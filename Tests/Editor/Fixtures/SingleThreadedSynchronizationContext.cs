using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Didionysymus.Lattice.Tests.Editor.Fixtures
{
    /// <summary>
    /// A synchronization context that queues every posted callback and runs them on the thread that calls
    /// <see cref="Run"/>, standing in for a single main thread without referencing an engine.
    /// </summary>
    public sealed class SingleThreadedSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<KeyValuePair<SendOrPostCallback, object>> _queue =
            new BlockingCollection<KeyValuePair<SendOrPostCallback, object>>();

        /// <summary>
        /// The managed thread ID of the thread that pumps this context; set by <see cref="Run"/>.
        /// </summary>
        public int ThreadID { get; private set; }

        /// <summary>
        /// Queues <paramref name="d"/> to run on the pumping thread.
        /// </summary>
        /// <param name="d">The callback.</param>
        /// <param name="state">The callback's argument.</param>
        public override void Post(SendOrPostCallback d, object state)
        {
            _queue.Add(new KeyValuePair<SendOrPostCallback, object>(d, state));
        }

        /// <summary>
        /// Not supported: nothing under test sends synchronously, and a send from another thread would block it.
        /// </summary>
        /// <param name="d">The callback.</param>
        /// <param name="state">The callback's argument.</param>
        /// <exception cref="NotSupportedException">Always.</exception>
        public override void Send(SendOrPostCallback d, object state)
        {
            throw new NotSupportedException("SingleThreadedSynchronizationContext does not support Send.");
        }

        /// <summary>
        /// Installs this context on the calling thread, starts <paramref name="action"/>, and runs posted callbacks on
        /// this thread until the task completes; then restores the previous context and rethrows the task's exception.
        /// Fails with <see cref="TimeoutException"/> instead of hanging when the task never completes.
        /// </summary>
        /// <param name="action">Starts the asynchronous work under this context.</param>
        /// <param name="timeout">How long to pump before giving up.</param>
        /// <exception cref="TimeoutException">Thrown when the task has not completed within <paramref name="timeout"/>.</exception>
        public void Run(Func<Task> action, TimeSpan timeout)
        {
            SynchronizationContext previous = Current;
            SetSynchronizationContext(this);
            ThreadID = Thread.CurrentThread.ManagedThreadId;

            try
            {
                Task task = action.Invoke();
                DateTime deadline = DateTime.UtcNow + timeout;
                while (!task.IsCompleted)
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new TimeoutException("The task did not complete while the context was pumped.");

                    if (_queue.TryTake(out KeyValuePair<SendOrPostCallback, object> item, 10)) item.Key.Invoke(item.Value);
                }

                task.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
