using System;

namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// Thrown before anything is bound when an object would have two owners: a second scope host in a batch outside a
    /// <see cref="ScopeBoundary"/>, a <see cref="LifetimeScopeBehaviour"/> inside one, or overlapping given roots. The
    /// message names each conflicting object as <c>{scene}/{hierarchy path}</c> and the fix.
    /// </summary>
    /// <remarks>
    /// <see cref="LifetimeScopeBehaviour"/> throws it from <c>Awake</c>; <see cref="ExplicitScopeHost.PrepareAsync(System.Threading.CancellationToken)"/>
    /// faults its task with it, changes nothing and stays attached; <see cref="ExplicitScopeHost.FindInScene"/> throws
    /// it when a scene holds more than one host.
    /// </remarks>
    public sealed class ScopeOwnershipException : InvalidOperationException
    {
        public ScopeOwnershipException(string message) : base(message) { }
    }
}