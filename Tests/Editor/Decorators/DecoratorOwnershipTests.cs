using System;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.Decorators
{
    /// <summary>
    /// Verifies that the container owns every object of a decorator chain it builds: each link of a Singleton or
    /// Scoped chain is disposed outermost first, initialized and started innermost first; a Transient's inner
    /// instance and each decorator are disposed with the requesting scope; and what a failed chain built is still
    /// disposed.
    ///
    /// Exercises <c>Scope.WrapDecorators</c>, <c>Scope.GetOrCreateCached</c> and <c>Scope.TrackTransient</c>.
    /// </summary>
    [TestFixture]
    public sealed class DecoratorOwnershipTests
    {
        /// <summary>
        /// Resets the static fixture state the tests read.
        /// </summary>
        [SetUp]
        public void Reset()
        {
            DisposalLog.Events.Clear();
            LifecycleEventLog.Events.Clear();
        }

        /// <summary>
        /// Disposing a scope releases every link of a decorated Scoped service, the outermost first.
        /// </summary>
        [Test]
        public void Dispose_WhenScopedServiceHasTwoDecorators_DisposesEveryLinkOutermostFirst()
        {
            IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IChained, ChainedLeaf>(Lifetime.Scoped);
                b.RegisterDecorator<IChained, InnerChainDecorator>(Lifetime.Scoped);
                b.RegisterDecorator<IChained, OuterChainDecorator>(Lifetime.Scoped);
            });
            resolver.Resolve<IChained>();

            resolver.Dispose();

            CollectionAssert.AreEqual(new[] { "outer:dispose", "inner:dispose", "leaf:dispose" }, DisposalLog.Events, "Every link is disposed, outermost first");
        }

        /// <summary>
        /// The pass initializes every link of a decorated Singleton, the innermost first.
        /// </summary>
        [Test]
        public void RunInitializables_WhenServiceHasTwoDecorators_InitializesEveryLinkInnermostFirst()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IChained, ChainedLeaf>(Lifetime.Singleton);
                b.RegisterDecorator<IChained, InnerChainDecorator>(Lifetime.Singleton);
                b.RegisterDecorator<IChained, OuterChainDecorator>(Lifetime.Singleton);
            });

            resolver.RunInitializables();

            CollectionAssert.AreEqual(new[] { "leaf:init", "inner:init", "outer:init" }, LifecycleEventLog.Events, "Every link is initialized, innermost first");
        }

        /// <summary>
        /// Startup starts every link of a decorated Singleton, the innermost first.
        /// </summary>
        [Test]
        public async Task RunAsyncStartablesAsync_WhenServiceHasTwoDecorators_StartsEveryLinkInnermostFirst()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IChained, ChainedLeaf>(Lifetime.Singleton);
                b.RegisterDecorator<IChained, InnerChainDecorator>(Lifetime.Singleton);
                b.RegisterDecorator<IChained, OuterChainDecorator>(Lifetime.Singleton);
            });
            resolver.RunInitializables();
            LifecycleEventLog.Events.Clear();

            await resolver.RunAsyncStartablesAsync(CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "leaf:start", "inner:start", "outer:start" }, LifecycleEventLog.Events, "Every link is started, innermost first");
        }

        /// <summary>
        /// A decorated Transient's inner instance and every decorator are disposed with the scope that resolved it.
        /// </summary>
        [Test]
        public void Dispose_WhenDecoratedTransientResolved_DisposesInnerAndEveryDecorator()
        {
            IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IChained, DisposableTransientLeaf>(Lifetime.Transient);
                b.RegisterDecorator<IChained, InnerChainDecorator>(Lifetime.Transient);
                b.RegisterDecorator<IChained, OuterChainDecorator>(Lifetime.Transient);
            });
            resolver.Resolve<IChained>();

            resolver.Dispose();

            CollectionAssert.AreEqual(new[] { "outer:dispose", "inner:dispose", "leaf:dispose" }, DisposalLog.Events, "The inner instance and every decorator are disposed, outermost first");
        }

        /// <summary>
        /// When a Transient's decorator fails to build, the inner instance already built is still disposed with the
        /// scope.
        /// </summary>
        [Test]
        public void Dispose_WhenTransientDecoratorConstructionFails_DisposesBuiltInnerInstance()
        {
            IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IChained, DisposableTransientLeaf>(Lifetime.Transient);
                b.RegisterDecorator<IChained, ThrowingChainDecorator>(Lifetime.Transient);
            });
            Assert.Catch<Exception>(() => resolver.Resolve<IChained>(), "Precondition: the chain fails");

            resolver.Dispose();

            CollectionAssert.AreEqual(new[] { "leaf:dispose" }, DisposalLog.Events, "The built inner instance is disposed");
        }
    }
}
