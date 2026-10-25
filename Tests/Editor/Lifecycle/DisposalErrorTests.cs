using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Verifies that cleanup is best effort, then reported: a throwing <c>Dispose</c>, a child scope's failure or a
    /// throwing cancellation callback never stops the rest of the cleanup, and <c>Dispose</c> then throws one flat
    /// <see cref="AggregateException"/> listing each failure in the order it happened; a second <c>Dispose</c> does
    /// nothing.
    ///
    /// Exercises the failure list of <c>Scope.Dispose</c> and <c>Scope.AddFailures</c>.
    /// </summary>
    [TestFixture]
    public sealed class DisposalErrorTests
    {
        /// <summary>
        /// Resets the static fixture state the tests read.
        /// </summary>
        [SetUp]
        public void Reset()
        {
            DisposalLog.Events.Clear();
        }

        /// <summary>
        /// A service whose <c>Dispose</c> throws does not stop the services before it from being disposed; the
        /// failure is reported afterwards in an <see cref="AggregateException"/>.
        /// </summary>
        [Test]
        public void Dispose_WhenServiceDisposeThrows_DisposesRemainingServicesAndThrowsAggregateException()
        {
            IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IDisposable>(_ => new LoggedDisposable("a"), Lifetime.Singleton);
                b.RegisterFactory<IDisposable>(_ => new ThrowingDisposable("b"), Lifetime.Singleton);
                b.RegisterFactory<IDisposable>(_ => new LoggedDisposable("c"), Lifetime.Singleton);
            });
            resolver.ResolveAll<IDisposable>();

            Exception caught = Record(() => resolver.Dispose());

            Assert.IsInstanceOf<AggregateException>(caught, "Dispose reports the failure after cleanup");
            AggregateException aggregate = (AggregateException)caught;
            Assert.AreEqual(1, aggregate.InnerExceptions.Count, "One failure is listed");
            Assert.IsInstanceOf<NotSupportedException>(aggregate.InnerExceptions[0], "The service's own exception is listed");
            Assert.AreEqual("b", aggregate.InnerExceptions[0].Message, "The failure is the throwing service's");
            CollectionAssert.AreEqual(new[] { "c:dispose", "b:dispose", "a:dispose" }, DisposalLog.Events, "Every service is disposed, in reverse order");
        }

        /// <summary>
        /// Several failures are listed each once, in the order disposal met them.
        /// </summary>
        [Test]
        public void Dispose_WhenSeveralServicesThrow_AggregateListsEachFailureInDisposalOrder()
        {
            IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IDisposable>(_ => new ThrowingDisposable("x"), Lifetime.Singleton);
                b.RegisterFactory<IDisposable>(_ => new ThrowingDisposable("y"), Lifetime.Singleton);
            });
            resolver.ResolveAll<IDisposable>();

            Exception caught = Record(() => resolver.Dispose());

            Assert.IsInstanceOf<AggregateException>(caught, "Dispose reports the failures after cleanup");
            string[] messages = ((AggregateException)caught).InnerExceptions.Select(e => e.Message).ToArray();
            CollectionAssert.AreEqual(new[] { "y", "x" }, messages, "Each failure is listed once, in disposal order");
        }

        /// <summary>
        /// A child scope's failure does not stop the parent's own disposal, and the parent reports it in one flat
        /// list rather than an aggregate nested in an aggregate.
        /// </summary>
        [Test]
        public void Dispose_WhenChildScopeServiceThrows_DisposesParentServicesAndReportsChildFailure()
        {
            IObjectResolver root = Container.Build(b => b.RegisterFactory<LoggedDisposable>(_ => new LoggedDisposable("root"), Lifetime.Singleton));
            IObjectResolver child = root.CreateChildScope(b => b.RegisterFactory<ThrowingDisposable>(_ => new ThrowingDisposable("child"), Lifetime.Scoped));
            root.Resolve<LoggedDisposable>();
            child.Resolve<ThrowingDisposable>();

            Exception caught = Record(() => root.Dispose());

            Assert.IsInstanceOf<AggregateException>(caught, "The root reports the child's failure");
            AggregateException aggregate = (AggregateException)caught;
            Assert.AreEqual(1, aggregate.InnerExceptions.Count, "One failure is listed");
            Assert.IsInstanceOf<NotSupportedException>(aggregate.InnerExceptions[0], "The list is flat: the child's own failure, not its aggregate");
            Assert.AreEqual("child", aggregate.InnerExceptions[0].Message, "The failure is the child service's");
            CollectionAssert.AreEqual(new[] { "child:dispose", "root:dispose" }, DisposalLog.Events, "The child is disposed first, then the root's services");
        }

        /// <summary>
        /// A callback on the participants' token that throws when <c>Dispose</c> cancels it is reported after
        /// cleanup, and does not stop the scope's services from being disposed.
        /// </summary>
        [Test]
        public async Task Dispose_WhenParticipantTokenCallbackThrows_ReportsCallbackExceptionAfterCleanup()
        {
            GatedStartable gated = new GatedStartable();
            IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton);
                b.RegisterFactory<LoggedDisposable>(_ => new LoggedDisposable("svc"), Lifetime.Singleton);
            });
            resolver.Resolve<LoggedDisposable>();
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(CancellationToken.None);
            gated.ReceivedToken.Register(() => throw new NotSupportedException("callback"));

            Exception caught = Record(() => resolver.Dispose());
            gated.Gate.SetResult(true);
            _ = await TaskCapture.CatchAsync(startup);

            Assert.IsInstanceOf<AggregateException>(caught, "Dispose reports the callback's failure");
            bool listed = ((AggregateException)caught).InnerExceptions.Any(e => e is NotSupportedException && e.Message == "callback");
            Assert.IsTrue(listed, "The callback's own exception is listed");
            CollectionAssert.Contains(DisposalLog.Events, "svc:dispose", "The services are disposed after the callback threw");
        }

        /// <summary>
        /// A second <c>Dispose</c> after one that reported failures does nothing: it neither throws nor disposes
        /// anything again.
        /// </summary>
        [Test]
        public void Dispose_WhenCalledAgainAfterFailure_DoesNothing()
        {
            IObjectResolver resolver = Container.Build(b => b.RegisterFactory<ThrowingDisposable>(_ => new ThrowingDisposable("b"), Lifetime.Singleton));
            resolver.Resolve<ThrowingDisposable>();
            _ = Record(() => resolver.Dispose());

            Exception second = Record(() => resolver.Dispose());

            Assert.IsNull(second, "A second Dispose throws nothing");
            CollectionAssert.AreEqual(new[] { "b:dispose" }, DisposalLog.Events, "A second Dispose disposes nothing again");
        }

        /// <summary>
        /// Runs <paramref name="action"/> and returns the exception it threw, or null when it completed.
        /// </summary>
        /// <param name="action">The call to run.</param>
        /// <returns>The exception, or null.</returns>
        private Exception Record(Action action)
        {
            try
            {
                action.Invoke();
            }
            catch (Exception exception)
            {
                return exception;
            }

            return null;
        }
    }
}
