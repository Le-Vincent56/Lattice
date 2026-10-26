using System;
using System.Collections.Generic;
using Didionysymus.Lattice.Runtime.Internal;

namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// The roles of each component type discovery has met, computed once per type: <c>Inject</c> from the core's
    /// injection plan, <c>Initialize</c> from <see cref="IInitializable"/>, <c>Host</c> for the two scene hosts and
    /// <c>Boundary</c> for <see cref="ScopeBoundary"/>.
    /// </summary>
    /// <remarks>
    /// Main thread only: an unsynchronized static touched after every entry point's thread check. The metadata survives
    /// play sessions when the domain is not reloaded, which is safe because a type's members do not change within a
    /// domain.
    /// </remarks>
    internal static class ComponentTypeCache
    {
        private static readonly Dictionary<Type, ComponentRoles> _roles = new Dictionary<Type, ComponentRoles>();

        /// <summary>
        /// Returns the roles of <paramref name="type"/>, computing and caching them on first request.
        /// </summary>
        /// <param name="type">A component's concrete type.</param>
        /// <returns>The type's roles; <see cref="ComponentRoles.None"/> when it has none.</returns>
        internal static ComponentRoles GetRoles(Type type)
        {
            if (_roles.TryGetValue(type, out ComponentRoles roles)) return roles;

            roles = ComponentRoles.None;
            if (InjectionPlanCache.HasInjectionMembers(type)) roles |= ComponentRoles.Inject;

            if (typeof(IInitializable).IsAssignableFrom(type)) roles |= ComponentRoles.Initialize;

            if (typeof(LifetimeScopeBehaviour).IsAssignableFrom(type) || type == typeof(ExplicitScopeHost))
                roles |= ComponentRoles.Host;

            if (type == typeof(ScopeBoundary)) roles |= ComponentRoles.Boundary;

            _roles.Add(type, roles);
            return roles;
        }
    }
}