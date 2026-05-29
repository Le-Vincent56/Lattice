using System;
using System.Collections.Generic;

namespace Didionysymus.Lattice.Tests.Editor.Fixtures
{
    // Reusable POCO test fixtures shared across DI resolution tests.
    // All identity-tracking uses Guid InstanceID so tests can assert "same instance" vs.
    // "different instance" without relying on object reference equality (which can be
    // misleading across boxing/proxies).

    /// <summary>
    /// Test service A: dependency-free leaf service.
    /// </summary>
    public interface IServiceA
    {
        Guid InstanceID { get; }
    }

    /// <summary>
    /// Test service B: depends on <see cref="IServiceA"/>.
    /// </summary>
    public interface IServiceB
    {
        Guid InstanceID { get; }
        IServiceA A { get; }
    }

    /// <summary>
    /// Test service C: depends on both <see cref="IServiceA"/> and <see cref="IServiceB"/>.
    /// </summary>
    public interface IServiceC
    {
        Guid InstanceID { get; }
        IServiceA A { get; }
        IServiceB B { get; }
    }

    /// <summary>
    /// Concrete <see cref="IServiceA"/>. No dependencies.
    /// </summary>
    public sealed class ServiceA : IServiceA
    {
        public Guid InstanceID { get; } = Guid.NewGuid();
    }

    /// <summary>
    /// Concrete <see cref="IServiceB"/>. Constructor-injects <see cref="IServiceA"/>.
    /// </summary>
    public sealed class ServiceB : IServiceB
    {
        public Guid InstanceID { get; } = Guid.NewGuid();
        public IServiceA A { get; }
        public ServiceB(IServiceA a) => A = a;
    }

    /// <summary>
    /// Concrete <see cef="IServiceC"/>. Constructor-injects both A and B.
    /// </summary>
    public sealed class ServiceC : IServiceC
    {
        public Guid InstanceID { get; } = Guid.NewGuid();
        public IServiceA A { get; }
        public IServiceB B { get; }
        public ServiceC(IServiceA a, IServiceB b) => (A, B) = (a, b);
    }

    /// <summary>
    /// Service that tracks its own disposal. Used to verify the container disposes
    /// IDisposable instances on scope dispose, in reverse-creation order.
    /// </summary>
    public sealed class DisposableService : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public Guid InstanceID { get; } = Guid.NewGuid();
        public void Dispose() => IsDisposed = true;
    }

    /// <summary>
    /// Service that incremenets a static counter on constructor.
    /// Used to verify lifetime semantics by counting how many times the container actually instantiates it.
    /// </summary>
    /// <remarks>
    /// Tests using this fixture should reset <see cref="InstanceCount"/> in a SetUp
    /// method; the static is shared across the test fixture lifetime, not per-test.
    /// </remarks>
    public sealed class TrackedService
    {
        public static int InstanceCount;
        public TrackedService() => InstanceCount++;
    }

    /// <summary>
    /// Pipeline stage marker; used in multi-binding tests to verif that
    /// <c>ResolveAll&lt;IPipelineStage&gt;()</c> returns all registered stages
    /// in registration order.
    /// </summary>
    public interface IPipelineStage
    {
        string Name { get; }
    }

    public sealed class StageOne : IPipelineStage
    {
        public string Name => "one";
    }

    public sealed class StageTwo : IPipelineStage
    {
        public string Name => "two";
    }

    public sealed class StageThree : IPipelineStage
    {
        public string Name => "three";
    }

    /// <summary>
    /// Consumer that takes a collection of pipelien stages via constructor injection.
    /// Used to verify <see cref="IReadOnlyList{T}"/> parameter resolution wires through
    /// to <c>ResolveAll&lt;IPipelineStage&gt;()<c/> per <c>ReflectionActivatorFactory</c>.
    /// </summary>
    public sealed class PipelineConsumer
    {
        public IReadOnlyList<IPipelineStage> Stages { get; }
        public PipelineConsumer(IReadOnlyList<IPipelineStage> stages) => Stages = stages;
    }
}