using System;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Verifies the lifetime of the token startup participants receive: it lives until the scope is disposed, which
    /// cancels it before anything is disposed; the caller's token reaches it only while startup runs; and a caller
    /// cancelling its own token after disposal meets nothing of the scope's.
    ///
    /// Exercises <c>Scope.BeginStart</c>'s participant source and caller registration, <c>Scope.ReleaseStartup</c> and
    /// the head of <c>Scope.Dispose</c>.
    /// </summary>
    [TestFixture]
    public sealed class ParticipantTokenTests
    {
        /// <summary>
        /// A token a participant kept after startup is cancelled by <c>Dispose</c> before the scope's services are
        /// disposed.
        /// </summary>
        [Test]
        public async Task Dispose_AfterStartup_CancelsParticipantTokenBeforeDisposingServices()
        {
            IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<TokenKeepingStartable>(Lifetime.Singleton);
                b.Register<TokenObservingDisposable>(Lifetime.Singleton);
            });
            TokenKeepingStartable startable = resolver.Resolve<TokenKeepingStartable>();
            TokenObservingDisposable observer = resolver.Resolve<TokenObservingDisposable>();
            resolver.RunInitializables();
            await resolver.RunAsyncStartablesAsync(CancellationToken.None);
            observer.Token = startable.ReceivedToken;

            resolver.Dispose();

            Assert.IsTrue(observer.TokenCanceledAtDispose, "The participant token is cancelled before services are disposed");
        }

        /// <summary>
        /// Cancelling the caller's token after startup ended does not reach the participant token.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenCallerTokenCanceledAfterStartup_DoesNotCancelParticipantToken()
        {
            using CancellationTokenSource caller = new CancellationTokenSource();
            using IObjectResolver resolver = Container.Build(b => b.Register<TokenKeepingStartable>(Lifetime.Singleton));
            TokenKeepingStartable startable = resolver.Resolve<TokenKeepingStartable>();
            resolver.RunInitializables();
            await resolver.RunAsyncStartablesAsync(caller.Token);

            caller.Cancel();

            Assert.IsFalse(startable.ReceivedToken.IsCancellationRequested, "The caller's token reaches participants only during startup");
        }

        /// <summary>
        /// Cancelling the caller's token while startup runs cancels the participant token and the startup.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenCallerTokenCanceledDuringStartup_CancelsParticipantToken()
        {
            GatedStartable gated = new GatedStartable();
            using CancellationTokenSource caller = new CancellationTokenSource();
            using IObjectResolver resolver = Container.Build(b => b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton));
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(caller.Token);

            caller.Cancel();
            bool tokenCanceled = gated.ReceivedToken.IsCancellationRequested;
            gated.Gate.SetResult(true);
            Exception caught = await TaskCapture.CatchAsync(startup);

            Assert.IsTrue(tokenCanceled, "The caller's token reaches the participant token during startup");
            Assert.IsInstanceOf<OperationCanceledException>(caught, "Startup completes as cancelled");
        }

        /// <summary>
        /// A caller that cancels its token after <c>Dispose</c>, while the participant still runs, meets no exception
        /// from the scope's disposed resources.
        /// </summary>
        [Test]
        public async Task Dispose_WhenStartupRunning_LaterCallerCancellationDoesNotThrow()
        {
            GatedStartable gated = new GatedStartable();
            using CancellationTokenSource caller = new CancellationTokenSource();
            IObjectResolver resolver = Container.Build(b => b.RegisterFactory<IAsyncStartable>(_ => gated, Lifetime.Singleton));
            resolver.RunInitializables();
            Task startup = resolver.RunAsyncStartablesAsync(caller.Token);
            resolver.Dispose();

            Exception thrown = Record(() => caller.Cancel());
            gated.Gate.SetResult(true);
            _ = await TaskCapture.CatchAsync(startup);

            Assert.IsNull(thrown, "The caller's own Cancel never reaches what disposal released");
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
