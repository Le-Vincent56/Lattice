using System;
using System.Threading;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Verifies the startup threading contract without an engine: startup continues on the caller's synchronization
    /// context between participants, so a participant after one whose task completed on a worker thread is still
    /// called on the caller's thread.
    ///
    /// Exercises the await in <c>Scope.RunAsyncStartablesAsync</c> under <see cref="SingleThreadedSynchronizationContext"/>.
    /// </summary>
    [TestFixture]
    public sealed class StartupContextTests
    {
        /// <summary>
        /// The participant after one that completed on a thread-pool thread runs on the caller's context and thread.
        /// </summary>
        [Test]
        public void RunAsyncStartablesAsync_WhenStartableCompletesOnWorkerThread_StartsNextOnCallerContext()
        {
            ContextRecordingStartable recorder = new ContextRecordingStartable();
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => new WorkerThreadStartable(), Lifetime.Singleton);
                b.RegisterFactory<IAsyncStartable>(_ => recorder, Lifetime.Singleton);
            });
            resolver.RunInitializables();
            SingleThreadedSynchronizationContext context = new SingleThreadedSynchronizationContext();

            context.Run(() => resolver.RunAsyncStartablesAsync(CancellationToken.None), TimeSpan.FromSeconds(10));

            Assert.IsTrue(recorder.Started, "The second participant starts");
            Assert.AreSame(context, recorder.RecordedContext, "It is called on the caller's synchronization context");
            Assert.AreEqual(context.ThreadID, recorder.RecordedThreadID, "It is called on the caller's thread");
        }
    }
}
