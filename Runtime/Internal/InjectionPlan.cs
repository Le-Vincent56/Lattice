namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// The complete binding plan for a concrete type, built once per type per process by
    /// <see cref="InjectionPlanCache"/>. Bundles every <c>[Inject]</c>-annotated member (fields
    /// and properties walked across the type hierarchy) with an optional <c>[Inject]</c> method.
    /// </summary>
    /// <remarks>
    /// <see cref="Method"/> is null when the type declares no <c>[Inject]</c> method;
    /// <c>Scope.Inject</c> must null-check before invoking the cached invoker.
    /// </remarks>
    internal sealed class InjectionPlan
    {
        /// <summary>
        /// Field and property bindings, in discovery order (most-derived first).
        /// </summary>
        public InjectableMember[] Members { get; }

        /// <summary>
        /// The single <c>[Inject]</c> method, or null when none was found.
        /// </summary>
        public InjectMethod Method { get; }

        public InjectionPlan(InjectableMember[] members, InjectMethod method)
        {
            Members = members;
            Method = method;
        }
    }
}