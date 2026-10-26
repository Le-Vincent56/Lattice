using System;

namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// The roles discovery reads from a component's type, cached once per type. A component may have several roles,
    /// one, or none; one with none is not touched.
    /// </summary>
    [Flags]
    internal enum ComponentRoles
    {
        /// <summary>
        /// No role: the component is neither bound nor initialized.
        /// </summary>
        None = 0,

        /// <summary>
        /// The type has injection members (an <c>[Inject]</c> field, writable property or method, inherited ones
        /// included), or its injection plan cannot be built; the component is passed to <c>Inject</c>.
        /// </summary>
        Inject = 1,

        /// <summary>
        /// The type implements <see cref="IInitializable"/>; the component is initialized after the scope's pass.
        /// </summary>
        Initialize = 2,

        /// <summary>
        /// The type is a scope host: <see cref="LifetimeScopeBehaviour"/> or a subclass, or <see cref="ExplicitScopeHost"/>.
        /// </summary>
        Host = 4,

        /// <summary>
        /// The type is <see cref="ScopeBoundary"/>.
        /// </summary>
        Boundary = 8
    }
}