using System;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Exceptions;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Verifies how startup reports a failed participant: a thrown <c>StartAsync</c>, a faulted or null task, or an
    /// <see cref="OperationCanceledException"/> nobody requested surfaces as <see cref="LifecycleException"/> with
    /// <see cref="LifecyclePhase.Start"/>, later participants do not start, and the scope refuses later startup.
    ///
    /// Exercises the failure path of <c>Scope.RunAsyncStartablesAsync</c> and <c>Scope.Fault</c>.
    /// </summary>
    [TestFixture]
    public sealed class StartupFailureTests
    {
        /// <summary>
        /// Resets the static fixture state the tests read.
        /// </summary>
        [SetUp]
        public void Reset()
        {
            CountingStartable.StartCount = 0;
        }

        /// <summary>
        /// A thrown <c>StartAsync</c> surfaces as <see cref="LifecycleException"/> naming the participant's type and
        /// the start phase, with the participant's exception inside.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenStartAsyncThrows_ThrowsLifecycleExceptionNamingParticipantAndStartPhase()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IAsyncStartable, ThrowingStartable>(Lifetime.Singleton));
            resolver.RunInitializables();

            Exception caught = await TaskCapture.CatchAsync(resolver.RunAsyncStartablesAsync(CancellationToken.None));

            Assert.IsInstanceOf<LifecycleException>(caught, "A thrown StartAsync surfaces as LifecycleException");
            LifecycleException failure = (LifecycleException)caught;
            Assert.AreEqual(typeof(ThrowingStartable), failure.ParticipantType, "The exception names the participant");
            Assert.AreEqual(LifecyclePhase.Start, failure.Phase, "The exception names the start phase");
            Assert.IsInstanceOf<NotSupportedException>(failure.InnerException, "The participant's exception is inside");
        }

        /// <summary>
        /// A failed participant stops startup: later participants do not start, and the faulted scope refuses a later
        /// startup with the fault inside.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenStartAsyncThrows_LaterParticipantNotStartedAndScopeRefusesLaterStartup()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IAsyncStartable, ThrowingStartable>(Lifetime.Singleton);
                b.Register<CountingStartable>(Lifetime.Singleton);
            });
            resolver.RunInitializables();
            _ = await TaskCapture.CatchAsync(resolver.RunAsyncStartablesAsync(CancellationToken.None));

            Exception repeat = await TaskCapture.CatchAsync(resolver.RunAsyncStartablesAsync(CancellationToken.None));

            Assert.AreEqual(0, CountingStartable.StartCount, "A participant after the failed one does not start");
            Assert.IsInstanceOf<InvalidOperationException>(repeat, "A faulted scope refuses later startup");
            Assert.IsInstanceOf<LifecycleException>(repeat.InnerException, "The refusal carries the fault");
        }

        /// <summary>
        /// An <see cref="OperationCanceledException"/> nobody requested is a failure, not a cancellation.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenParticipantThrowsCanceledWithoutCancellation_ThrowsLifecycleException()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IAsyncStartable, UnpromptedCancelStartable>(Lifetime.Singleton));
            resolver.RunInitializables();

            Exception caught = await TaskCapture.CatchAsync(resolver.RunAsyncStartablesAsync(CancellationToken.None));

            Assert.IsInstanceOf<LifecycleException>(caught, "An unrequested cancellation is a failure");
            Assert.IsInstanceOf<OperationCanceledException>(caught.InnerException, "The participant's exception is inside");
        }

        /// <summary>
        /// A participant that returns no task is reported as a failed participant.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenStartAsyncReturnsNullTask_ThrowsLifecycleException()
        {
            using IObjectResolver resolver = Container.Build(b => b.Register<IAsyncStartable, NullTaskStartable>(Lifetime.Singleton));
            resolver.RunInitializables();

            Exception caught = await TaskCapture.CatchAsync(resolver.RunAsyncStartablesAsync(CancellationToken.None));

            Assert.IsInstanceOf<LifecycleException>(caught, "A null task is a failed participant");
            Assert.AreEqual(typeof(NullTaskStartable), ((LifecycleException)caught).ParticipantType, "The exception names the participant");
        }
    }
}
