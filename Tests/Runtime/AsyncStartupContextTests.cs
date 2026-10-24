using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Runtime.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Didionysymus.Lattice.Tests.Runtime
{
    /// <summary>
    /// Pins the startup threading contract under Unity's own synchronization context: called from the main thread,
    /// startup calls every participant on the main thread, including one after a participant whose task completed on
    /// a worker thread. Uses no scope host.
    /// </summary>
    [TestFixture]
    public sealed class AsyncStartupContextTests
    {
        /// <summary>
        /// The participant after one that completed on a thread-pool thread runs on the main thread.
        /// </summary>
        /// <returns>The coroutine the test runner drives frame by frame.</returns>
        [UnityTest]
        public IEnumerator RunAsyncStartablesAsync_WhenStartableCompletesOnWorkerThread_StartsNextOnMainThread()
        {
            int mainThreadID = Thread.CurrentThread.ManagedThreadId;
            ThreadRecordingStartable recorder = new ThreadRecordingStartable();
            IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => new WorkerThreadStartable(), Lifetime.Singleton);
                b.RegisterFactory<IAsyncStartable>(_ => recorder, Lifetime.Singleton);
            });
            resolver.RunInitializables();

            Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!startup.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            resolver.Dispose();
            Assert.IsTrue(startup.IsCompleted, "Startup completes");
            Assert.IsTrue(recorder.Started, "The second participant starts");
            Assert.AreEqual(mainThreadID, recorder.RecordedThreadID, "It is called on the main thread");
        }
    }
}
