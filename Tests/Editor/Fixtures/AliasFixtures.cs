using System;
using System.Threading;
using System.Threading.Tasks;
using Didionysymus.Lattice.Runtime;

namespace Didionysymus.Lattice.Tests.Editor.Fixtures
{
    /// <summary>
    /// First service type of an aliased registration. Paired with <see cref="IAliasSecondary"/> so one
    /// implementation can be bound under two service types with <c>As&lt;T&gt;()</c>.
    /// </summary>
    public interface IAliasPrimary
    {
        Guid InstanceID { get; }
    }

    /// <summary>
    /// Second service type of an aliased registration.
    /// </summary>
    public interface IAliasSecondary
    {
        Guid InstanceID { get; }
    }

    /// <summary>
    /// Implementation bound under several service types in alias-identity tests. Counts <see cref="Initialize"/>
    /// calls across every instance in a static field, so a test can tell one shared instance from one instance per
    /// alias. Tests reset <see cref="InitializeCount"/> in <c>[SetUp]</c>.
    /// </summary>
    public sealed class AliasedService : IAliasPrimary, IAliasSecondary, IInitializable
    {
        public static int InitializeCount;
        public Guid InstanceID { get; } = Guid.NewGuid();
        public void Initialize() => InitializeCount++;
    }

    /// <summary>
    /// <see cref="IAsyncStartable"/> that <c>AsImplementedInterfaces()</c> binds under its own type and under
    /// <see cref="IAsyncStartable"/>, two buckets <c>RunAsyncStartablesAsync</c> both selects. Counts
    /// <see cref="StartAsync"/> calls across every instance in a static field; tests reset <see cref="StartCount"/> in
    /// <c>[SetUp]</c>.
    /// </summary>
    public sealed class AliasedStartable : IAsyncStartable, IAliasPrimary
    {
        public static int StartCount;
        public Guid InstanceID { get; } = Guid.NewGuid();

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.CompletedTask;
        }
    }
}
