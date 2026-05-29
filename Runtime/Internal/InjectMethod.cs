using System;
using System.Reflection;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Pure-metadata description of the (at-most-one) <c>[Inject]</c>-annotated method
    /// discovered on a type. The <see cref="Invoker"/> delegate is compiled by <see cref="InjectionPlanCache"/>
    /// via expression trees so method dispatch in <c>Scope.Inject</c> avoids reflection at runtime.
    /// </summary>
    internal sealed class InjectMethod
    {
        /// <summary>
        /// The reflected method. Retained for diagnostics and per-parameter <c>[Inject(Optional = ...)]</c> lookups.
        /// </summary>
        public MethodInfo Method { get; }

        /// <summary>
        /// The method's parameter list, captured once at plan-build time. Each parameter is resolved individually by <c>Scope.Inject</c>
        /// before invocation; per-parameter <c>[Inject(Optional = true)]</c> is honored via reflection on the <see cref="ParameterInfo"/>.
        /// </summary>
        public ParameterInfo[] Parameters { get; }

        /// <summary>
        /// (instance, args[]) =&gt; ((Concrete)instance).Method((P0)args[0], (P1)args[1], ...).
        /// Compiled once per type per process by <see cref="InjectionPlanCache"/>.
        /// </summary>
        public Action<object, object[]> Invoker { get; }

        public InjectMethod(MethodInfo method, ParameterInfo[] parameters, Action<object, object[]> invoker)
        {
            Method = method;
            Parameters = parameters;
            Invoker = invoker;
        }
    }
}