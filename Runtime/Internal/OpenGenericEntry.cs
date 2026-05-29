using System;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Internal data record describing an open-generic registration: the open-service type definition
    /// (e.g. <c>typeof(IRepository&lt;&gt;)</c>, the open implementation-type definition
    /// (e.g., <c>typeof(Repository&lt;&gt;)</c>, and the lifetime applied to every closed instantiation.
    /// </summary>
    /// <remarks>
    /// Stored in <see cref="Registry.OpenRegistry"/> keyed by <see cref="OpenServiceType"/>.
    /// Promotion to a closed <see cref="RegistrationEntry"/> happens lazily via <c>Scope.PromoteOpenGeneric</c>
    /// on first resolve of a closed type, or eagerly via <c>Registry.PrebuildPreservedClosedGenerics</c> for any
    /// type declared in <see cref="Registry.PreservedClosedGenerics"/>.
    /// </remarks>
    internal sealed class OpenGenericEntry
    {
        /// <summary>
        /// Open generic service-type definition (e.g., <c>typeof(IRepository&lt;&gt;)</c>).
        /// </summary>
        public Type OpenServiceType { get; }

        /// <summary>
        /// Open generic implementation-type definition (e.g.< <c>typeof(Repository&lt;&gt;)</c>).
        /// </summary>
        public Type OpenImplType { get; }

        /// <summary>
        /// Lifetime applied to every closed-on-demand <see cref="RegistrationEntry"/>
        /// promoted from this entry.
        /// </summary>
        public Lifetime Lifetime { get; }

        public OpenGenericEntry(Type openServiceType, Type openImplType, Lifetime lifetime)
        {
            OpenServiceType = openServiceType;
            OpenImplType = openImplType;
            Lifetime = lifetime;
        }
    }
}