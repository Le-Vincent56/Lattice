using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Internal;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Lifecycle
{
    /// <summary>
    /// Verifies that nothing escapes disposal: after <c>Dispose</c> a scope tracks nothing it cached, initialized or
    /// was to dispose; an object built while disposal runs, on another thread or by a factory that disposes its own
    /// scope, is disposed instead of tracked and its resolve throws <see cref="ObjectDisposedException"/>; a pass that
    /// observes the disposal stops; a child is never added to a disposed parent; everything a failed pass or startup
    /// built is still disposed; and a re-entrant <c>Dispose</c> disposes nothing twice.
    ///
    /// Exercises <c>Scope.Dispose</c>'s snapshots and clears, the disposed checks in <c>Scope.MaterializeFromEntry</c>,
    /// <c>Scope.GetOrCreateCached</c>, <c>Scope.InitializeTarget</c> and the <c>Scope</c> constructor, and
    /// <c>Scope.IsTracking</c>.
    /// </summary>
    [TestFixture]
    public sealed class DisposalReleaseTests
    {
        /// <summary>
        /// Resets the static fixture state the tests read.
        /// </summary>
        [SetUp]
        public void Reset()
        {
            DisposalLog.Events.Clear();
            GatedDisposableTransient.Entered.Reset();
            GatedDisposableTransient.Gate.Reset();
            GatedDisposableTransient.LastCreated = null;
            ScopeDisposingInitializable.Target = null;
            ReentrantDisposable.Target = null;
        }

        /// <summary>
        /// A supplied object the scope initialized is no longer tracked once the scope is disposed.
        /// </summary>
        [Test]
        public void Dispose_WhenSuppliedObjectWasInitialized_ScopeNoLongerTracksIt()
        {
            IObjectResolver resolver = Container.Build(_ => { });
            resolver.RunInitializables();
            InitTracking supplied = new InitTracking();
            resolver.InitializeInstance(supplied);
            Scope scope = (Scope)resolver;
            Assert.IsTrue(scope.IsTracking(supplied), "Precondition: the scope tracks the object it initialized");

            resolver.Dispose();

            Assert.IsFalse(scope.IsTracking(supplied), "A disposed scope no longer tracks the object it initialized");
        }

        /// <summary>
        /// A disposed root no longer holds the Singletons it cached and initialized.
        /// </summary>
        [Test]
        public void Dispose_WhenRootDisposed_RootNoLongerTracksSingletons()
        {
            IObjectResolver resolver = Container.Build(b => b.Register<InitTracking>(Lifetime.Singleton));
            InitTracking instance = resolver.Resolve<InitTracking>();
            resolver.RunInitializables();
            Scope scope = (Scope)resolver;
            Assert.IsTrue(scope.IsTracking(instance), "Precondition: the root tracks its Singleton");

            resolver.Dispose();

            Assert.IsFalse(scope.IsTracking(instance), "A disposed root no longer tracks its Singletons");
        }

        /// <summary>
        /// Repeated child sessions leave nothing behind: the parent never tracks what a child built or initialized,
        /// the disposed child tracks nothing, the parent's child list is empty, and each owned object is disposed
        /// once.
        /// </summary>
        [Test]
        public void Dispose_WhenChildSessionsRepeated_ParentTracksNothingFromThem()
        {
            IObjectResolver root = Container.Build(b => b.Register<InitTracking>(Lifetime.Singleton));
            root.RunInitializables();
            Scope rootScope = (Scope)root;
            List<object> kept = new List<object>();
            List<LoggedDisposable> owned = new List<LoggedDisposable>();
            List<Scope> children = new List<Scope>();

            for (int round = 0; round < 3; round++)
            {
                IObjectResolver child = root.CreateChildScope(b =>
                {
                    b.RegisterFactory<LoggedDisposable>(_ => new LoggedDisposable("scoped"), Lifetime.Scoped);
                    b.RegisterFactory<IDisposable>(_ => new LoggedDisposable("transient"), Lifetime.Transient);
                });
                child.RunInitializables();
                LoggedDisposable scoped = child.Resolve<LoggedDisposable>();
                LoggedDisposable transient = (LoggedDisposable)child.Resolve<IDisposable>();
                InitTracking supplied = new InitTracking();
                child.InitializeInstance(supplied);
                kept.Add(scoped);
                kept.Add(transient);
                kept.Add(supplied);
                owned.Add(scoped);
                owned.Add(transient);
                children.Add((Scope)child);
                child.Dispose();
            }

            for (int i = 0; i < kept.Count; i++)
            {
                Assert.IsFalse(rootScope.IsTracking(kept[i]), "The parent never tracks what a child built or initialized");
                for (int c = 0; c < children.Count; c++)
                {
                    Assert.IsFalse(children[c].IsTracking(kept[i]), "A disposed child tracks nothing");
                }
            }

            Assert.AreEqual(0, rootScope.Children.Count, "Every disposed child left the parent's child list");
            for (int i = 0; i < owned.Count; i++)
            {
                Assert.AreEqual(1, owned[i].DisposeCount, "Each Scoped and Transient object is disposed once");
            }
        }

        /// <summary>
        /// A disposable Transient whose construction finishes after its scope's disposal began is disposed, not
        /// tracked, and its resolve throws <see cref="ObjectDisposedException"/>.
        /// </summary>
        [Test]
        public async Task Dispose_WhenTransientCompletesAfterDisposalBegan_DisposesItAndThrowsObjectDisposedException()
        {
            IObjectResolver resolver = Container.Build(b => b.Register<GatedDisposableTransient>(Lifetime.Transient));
            Task<Exception> worker = Task.Run(() => Record(() => resolver.Resolve<GatedDisposableTransient>()));
            Assert.IsTrue(GatedDisposableTransient.Entered.Wait(5000), "Precondition: the resolve is building the object");

            resolver.Dispose();
            GatedDisposableTransient.Gate.Set();
            Exception caught = await worker;

            Assert.IsInstanceOf<ObjectDisposedException>(caught, "A resolve that completes after disposal began throws");
            Assert.IsTrue(GatedDisposableTransient.LastCreated.IsDisposed, "The late object is disposed, not leaked");
        }

        /// <summary>
        /// An <c>Initialize</c> that disposes its own scope stops the pass before the next participant, and the scope
        /// keeps no record of the participant that disposed it.
        /// </summary>
        [Test]
        public void RunInitializables_WhenInitializeDisposesScope_StopsBeforeNextParticipantAndTracksNothing()
        {
            IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<ScopeDisposingInitializable>(Lifetime.Singleton);
                b.Register<InitTracking>(Lifetime.Singleton);
            });
            ScopeDisposingInitializable.Target = resolver;
            ScopeDisposingInitializable disposing = resolver.Resolve<ScopeDisposingInitializable>();
            InitTracking next = resolver.Resolve<InitTracking>();

            Exception caught = Record(() => resolver.RunInitializables());

            Assert.IsInstanceOf<ObjectDisposedException>(caught, "The pass reports that the scope was disposed");
            Assert.AreEqual(0, next.InitializeCallCount, "No participant is initialized after the disposal");
            Assert.IsFalse(((Scope)resolver).IsTracking(disposing), "The disposed scope does not track the disposing participant");
        }

        /// <summary>
        /// A pass that failed midway still leaves every participant it built to be disposed with the scope, those
        /// after the failure included.
        /// </summary>
        [Test]
        public void Dispose_WhenPassFailedMidway_DisposesEveryConstructedParticipant()
        {
            IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<DisposableInitializable>(_ => new DisposableInitializable("first"), Lifetime.Singleton);
                b.Register<ThrowingInitializable>(Lifetime.Singleton);
                b.RegisterFactory<DisposableInitializable>(_ => new DisposableInitializable("last"), Lifetime.Singleton);
            });
            Assert.Catch<Exception>(() => resolver.RunInitializables(), "Precondition: the pass fails midway");

            resolver.Dispose();

            CollectionAssert.Contains(DisposalLog.Events, "first:dispose", "A participant initialized before the failure is disposed");
            CollectionAssert.Contains(DisposalLog.Events, "last:dispose", "A participant built but never initialized is disposed");
        }

        /// <summary>
        /// A failed startup leaves the participants that started to be disposed with the scope.
        /// </summary>
        [Test]
        public async Task Dispose_WhenStartupFailed_DisposesStartedParticipants()
        {
            IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<DisposableStartable>(_ => new DisposableStartable("started"), Lifetime.Singleton);
                b.Register<ThrowingStartable>(Lifetime.Singleton);
            });
            resolver.RunInitializables();
            Exception failed = await TaskCapture.CatchAsync(resolver.RunAsyncStartablesAsync(CancellationToken.None));
            Assert.IsNotNull(failed, "Precondition: startup fails");

            resolver.Dispose();

            CollectionAssert.Contains(DisposalLog.Events, "started:dispose", "A participant of a failed startup is disposed");
        }

        /// <summary>
        /// A parent disposed while a child's registrations are being configured refuses the child, so no child is
        /// left that nothing disposes.
        /// </summary>
        [Test]
        public void CreateChildScope_WhenParentDisposedDuringConfigure_ThrowsObjectDisposedException()
        {
            IObjectResolver root = Container.Build(_ => { });

            Exception caught = Record(() => root.CreateChildScope(_ => root.Dispose()));

            Assert.IsInstanceOf<ObjectDisposedException>(caught, "A disposed parent refuses the child");
            Assert.AreEqual(0, ((Scope)root).Children.Count, "No child was added to the disposed parent");
        }

        /// <summary>
        /// A Singleton factory that disposes its own scope gets its object disposed rather than cached, and the
        /// resolve throws <see cref="ObjectDisposedException"/>.
        /// </summary>
        [Test]
        public void Resolve_WhenFactoryDisposesItsScope_DisposesTheInstanceAndThrowsObjectDisposedException()
        {
            LoggedDisposable made = null;
            IObjectResolver resolver = null;
            Func<IObjectResolver, LoggedDisposable> factory = _ =>
            {
                resolver.Dispose();
                made = new LoggedDisposable("made");
                return made;
            };
            resolver = Container.Build(b => b.RegisterFactory(factory, Lifetime.Singleton));

            Exception caught = Record(() => resolver.Resolve<LoggedDisposable>());

            Assert.IsInstanceOf<ObjectDisposedException>(caught, "The resolve reports that the scope was disposed");
            Assert.AreEqual(1, made.DisposeCount, "The object built after the disposal is disposed once");
            Assert.IsFalse(((Scope)resolver).IsTracking(made), "The disposed scope does not track it");
        }

        /// <summary>
        /// A service whose <c>Dispose</c> disposes its own scope meets the latch: the inner call returns at once, the
        /// outer call finishes, and nothing is disposed twice.
        /// </summary>
        [Test]
        public void Dispose_WhenServiceDisposesItsScope_ReturnsWithoutDisposingTwice()
        {
            IObjectResolver resolver = Container.Build(b =>
            {
                b.RegisterFactory<LoggedDisposable>(_ => new LoggedDisposable("a"), Lifetime.Singleton);
                b.Register<ReentrantDisposable>(Lifetime.Singleton);
            });
            ReentrantDisposable.Target = resolver;
            resolver.Resolve<LoggedDisposable>();
            resolver.Resolve<ReentrantDisposable>();

            Exception caught = Record(() => resolver.Dispose());

            Assert.IsNull(caught, "The re-entrant Dispose neither throws nor stops the outer one");
            CollectionAssert.AreEqual(new[] { "reentrant:dispose", "a:dispose" }, DisposalLog.Events, "Each object is disposed once, in reverse order");
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
