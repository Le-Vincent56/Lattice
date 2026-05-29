using System.Collections.Generic;
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Tests.Editor.Fixtures;
using NUnit.Framework;

namespace Didionysymus.Lattice.Tests.Editor.MultiBinding
{
    /// <summary>
    /// Verifies the multi-binding contract:
    /// <list type="bullet">
    ///     <item><c>ResolveAll&lt;T&gt;</c> returns all registered implementations in registration order</item>
    ///     <item><c>Resolve&lt;T&gt;</c> with multiple registrations returns the last-registered implementation</item>
    ///     <item>constructor parameters of <see cref="IReadOnlyList{T}"/> auto-resolve via <c>ResolveAll</c></item>
    ///     <item>cross-scope <c>ResolveAll</c> returns parent registrations first, then child</item>
    /// </list>
    ///
    /// Exercises <c>Scope.ResolveAll</c>, <c>Scope.CollectAllEntries</c>'s parent-first walk,
    /// and <c>ReflectionActivatorFactory.ResolveParameters</c>'s collection-parameter detection.
    /// </summary>
    [TestFixture]
    public sealed class MultiBindingTests
    {
        /// <summary>
        /// <c>ResolveAll</c> with three registrations must return all three in registration
        /// order. Verifies <c>Registry.ClosedRegistry</c> preserves insertion order
        /// and <c>CollectAllEntries</c> iterates that order.
        /// </summary>
        [Test]
        public void ResolveAll_WhenMultipleImplementationsRegistered_ReturnsAllInRegistrationOrder()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IPipelineStage, StageOne>(Lifetime.Transient);
                b.Register<IPipelineStage, StageTwo>(Lifetime.Transient);
                b.Register<IPipelineStage, StageThree>(Lifetime.Transient);
            });

            IReadOnlyList<IPipelineStage> stages = resolver.ResolveAll<IPipelineStage>();

            string[] names = new string[stages.Count];
            for (int i = 0; i < stages.Count; i++)
            {
                names[i] = stages[i].Name;
            }

            Assert.That(names, Is.EqualTo(new[] { "one", "two", "three" }));
        }

        /// <summary>
        /// Single <c>Resolve&lt;T&gt;</c> with multiple registrations must
        /// return the last-registered implementation. Verifies <c>ResolveInternal</c>'s
        /// <c>entries[^1]</c> pick.
        /// </summary>
        [Test]
        public void Resolve_WhenMultipleImplementationsRegistered_ReturnsLastRegistered()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IPipelineStage, StageOne>(Lifetime.Transient);
                b.Register<IPipelineStage, StageTwo>(Lifetime.Transient);
                b.Register<IPipelineStage, StageThree>(Lifetime.Transient);
            });

            IPipelineStage single = resolver.Resolve<IPipelineStage>();

            Assert.That(single.Name, Is.EqualTo("three"));
        }

        /// <summary>
        /// A constructor taking <see cref="IReadOnlyList{T}"/> must receive all registered
        /// implementations injected via the <c>ResolveAll</c> path. Verifies
        /// <c>ReflectionActivatorFactory.ResolveParameters</c>'s collection-parameter
        /// detection reaches into <c>IObjectResolver.ResolveAll&lt;T&gt;</c> at construction time.
        /// </summary>
        [Test]
        public void Resolve_WhenConstructorTakesIReadOnlyList_AutoResolvesAllImplementations()
        {
            using IObjectResolver resolver = Container.Build(b =>
            {
                b.Register<IPipelineStage, StageOne>(Lifetime.Transient);
                b.Register<IPipelineStage, StageTwo>(Lifetime.Transient);
                b.Register<IPipelineStage, StageThree>(Lifetime.Transient);
                b.Register<PipelineConsumer>(Lifetime.Transient);
            });

            PipelineConsumer consumer = resolver.Resolve<PipelineConsumer>();

            Assert.That(consumer.Stages.Count, Is.EqualTo(3));

            string[] names = new string[consumer.Stages.Count];
            for (int i = 0; i < consumer.Stages.Count; i++)
            {
                names[i] = consumer.Stages[i].Name;
            }

            Assert.That(names, Is.EqualTo(new[] { "one", "two", "three" }));
        }

        /// <summary>
        /// When registrations span parent and child scopes, <c>ResolveAll</c> must return
        /// parent registrations first, then child; both in registration order. Verifies
        /// <c>CollectInto</c>'s parent-first recursion in <c>Scope.cs</c>
        /// </summary>
        [Test]
        public void ResolveAll_WhenRegistrationsSpanParentAndChild_ReturnsParentRegistrationsFirst()
        {
            using IObjectResolver root = Container.Build(b =>
            {
                b.Register<IPipelineStage, StageOne>(Lifetime.Transient);
                b.Register<IPipelineStage, StageTwo>(Lifetime.Transient);
            });
            using IObjectResolver child = root.CreateChildScope(b =>
            {
                b.Register<IPipelineStage, StageThree>(Lifetime.Transient);
            });

            IReadOnlyList<IPipelineStage> stages = child.ResolveAll<IPipelineStage>();

            string[] names = new string[stages.Count];
            for (int i = 0; i < stages.Count; i++)
            {
                names[i] = stages[i].Name;
            }

            Assert.That(
                names,
                Is.EqualTo(new[] { "one", "two", "three" }),
                "Parent registrations come first; child registrations append"
            );
        }
    }
}