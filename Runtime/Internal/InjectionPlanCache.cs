using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Didionysymus.Lattice.Runtime.Exceptions;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Process-static cache of <see cref="InjectionPlan"/>s keyed by concrete type. Walks the
    /// type hierarchy (most-derived first) to discover <c>Inject</c>-annotated fields, properties (including private setters),
    /// and methods (including private and inherited members declared by base classes). Per-member setter
    /// delegates and the optional method invoker are compiled via <see cref="System.Linq.Expressions"/> so member injection
    /// in <c>Scope.Inject</c> is AOT-safe and avoids reflection at the per-resolve hot path; mirrors the
    /// AOT strategy used by <see cref="ExpressionActivatorFactory"/>.
    /// </summary>
    /// <remarks>
    /// The cache is process-static. In the Unity Editor, domain reloads wipe it naturally; in built
    /// players  it persists for the player lifetime. <see cref="CachedPlanCount"/> is exposed for the EditMode cache-hit test and reads the
    /// live <see cref="ConcurrentDictionary{TKey, TValue}.Count"/>; tests should assert deltas rather than absolute
    /// counts to remain robust to prior tests in the run priming the cache.
    /// </remarks>
    internal static class InjectionPlanCache
    {
        /// <summary>
        /// <see cref="BindingFlags"/> applied to every reflective member walk. Combined with
        /// <see cref="BindingFlags.DeclaredOnly"/>, this discovers public and non-public instance members
        /// declared on a single level of the type hierarchy; the hierarchy walk in <see cref="BuildPlan"/> re-applies these flags per
        /// <see cref="Type.BaseType"/> hop so inherited <c>[Inject]</c> members are picked up exactly once per declaring type.
        /// </summary>
        private const BindingFlags MemberFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                                 BindingFlags.DeclaredOnly;

        private static readonly ConcurrentDictionary<Type, InjectionPlan> _plans =
            new ConcurrentDictionary<Type, InjectionPlan>();

        /// <summary>
        /// Live count of plans currently in the cache. Exposed for the cache-hit assertion in
        /// EditMode tests via <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/>;
        /// not part of the supported public API.
        /// </summary>
        public static int CachedPlanCount => _plans.Count;

        /// <summary>
        /// Returns the cached <see cref="InjectionPlan"/> for <paramref name="type"/>, building and
        /// caching a new plan on first request. Thread-safe via <see cref="ConcurrentDictionary{TKey, TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/>;
        /// concurrent first-requests for the same type may race in <see cref="BuildPlan"/>, but the dictionary discards the loesr's plan and returns one canonical instance
        /// to all callers.
        /// </summary>
        /// <param name="type">
        /// The <see cref="Type"/> for which the injection plan is being fetched or created. This type
        /// is used as the key to locate an existing plan or to build a new one.
        /// </param>
        /// <returns>An <see cref="InjectionPlan"/> representing the injection logic and member resolution for the specified <paramref name="type"/>.</returns>
        public static InjectionPlan GetPlan(Type type) => _plans.GetOrAdd(type, BuildPlan);

        /// <summary>
        /// Builds a complete <see cref="InjectionPlan"/> for <paramref name="type"/> by walking the type hierarchy
        /// from most-derived to (but not including) <see cref="object"/>. Discovers ever <c>[Inject]</c>-annotated field and property declared
        /// on each level (private members included), then resolves the at-most-one <c>[Inject]</c> method via <see cref="BuildInjectMethod"/>.
        /// </summary>
        /// <param name="type">
        /// The <see cref="Type"/> for which the injection plan is being constructed.
        /// This includes traversing its hierarchy to process declared members.
        /// </param>
        /// <returns>An <see cref="InjectionPlan"/> containing the resolved members and injection logic for the specified <paramref name="type"/>.</returns>
        private static InjectionPlan BuildPlan(Type type)
        {
            List<InjectableMember> members = new List<InjectableMember>();
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo field in t.GetFields(MemberFlags))
                {
                    InjectAttribute attribute = field.GetCustomAttribute<InjectAttribute>(inherit: false);
                    if (attribute == null) continue;

                    members.Add(new InjectableMember(
                        field.Name,
                        field.FieldType,
                        attribute.Optional,
                        BuildFieldSetter(field)
                    ));
                }

                foreach (PropertyInfo property in t.GetProperties(MemberFlags))
                {
                    // Read-only properties (no setter, including init-only after constructor) cannot be Phase-1 bound.
                    if (!property.CanWrite) continue;

                    InjectAttribute attribute = property.GetCustomAttribute<InjectAttribute>(inherit: false);
                    if (attribute == null) continue;

                    members.Add(new InjectableMember(
                        property.Name,
                        property.PropertyType,
                        attribute.Optional,
                        BuildPropertySetter(property)
                    ));
                }
            }

            InjectMethod method = BuildInjectMethod(type);
            return new InjectionPlan(members.ToArray(), method);
        }

        /// <summary>
        /// Compiles <c>(object instance, object value) =&gt; ((Concrete)instance).field = (FieldType)value</c> for the supplied field.
        /// The double-cast (instance and value) lets teh delegate's signature stay uniform across all field types while the compiled body
        /// assigned the strongly-typed field directly.
        /// </summary>
        /// <param name="field">The <see cref="FieldInfo"/> instance representing the field to be set.</param>
        /// <returns>A compiled delegate of type <see cref="Action{T1, T2}"/> that sets the value of the specified field.</returns>
        private static Action<object, object> BuildFieldSetter(FieldInfo field)
        {
            ParameterExpression instParam = Expression.Parameter(typeof(object), "instance");
            ParameterExpression valParam = Expression.Parameter(typeof(object), "value");
            UnaryExpression instCast = Expression.Convert(instParam, field.DeclaringType!);
            UnaryExpression valCast = Expression.Convert(valParam, field.FieldType);
            MemberExpression fieldAccess = Expression.Field(instCast, field);
            BinaryExpression assign = Expression.Assign(fieldAccess, valCast);

            return Expression.Lambda<Action<object, object>>(assign, instParam, valParam).Compile();
        }

        /// <summary>
        /// Compiles <c>(object instance, object value) =&gt; ((Concrete)instance).Property = (PropertyType)value</c> for the supplied property.
        /// Uses the non-public setter accessor so private setters annotated for injection (<c>public T Prop { get; private set; }</c> are reachable.
        /// </summary>
        /// <param name="property">The <see cref="PropertyInfo"/> describing the property for which a setter delegate is to be created.</param>
        /// <returns>A compiled delegate of type <see cref="Action{Object, Object}"/> that sets the value of the specified property.</returns>
        private static Action<object, object> BuildPropertySetter(PropertyInfo property)
        {
            ParameterExpression instParam = Expression.Parameter(typeof(object), "instance");
            ParameterExpression valParam = Expression.Parameter(typeof(object), "value");
            UnaryExpression instCast = Expression.Convert(instParam, property.DeclaringType!);
            UnaryExpression valCast = Expression.Convert(valParam, property.PropertyType);
            MethodCallExpression setterCall =
                Expression.Call(instCast, property.GetSetMethod(nonPublic: true), valCast);

            return Expression.Lambda<Action<object, object>>(setterCall, instParam, valParam).Compile();
        }

        /// <summary>
        /// Walks the type hierarchy looking for <c>[Inject]</c>-annotated methods, stopping at the first level
        /// (most-derived first) that declares one. If multiple methods at that level carry the attribute, throws
        /// <see cref="MultipleInjectMethodsException"/>. A derived class's <c>[Inject]</c> method shadows any base-class
        /// <c>[Inject]</c> methods entirely; base-level methods are ignored. Returns null
        /// when no <c>[Inject]</c> method is found anywhere in the hierarchy.
        /// </summary>
        /// <param name="type">The type whose injection method is to be determined.</param>
        /// <returns>An <see cref="InjectMethod"/> instance encapsulating the identified method, its parameters, and a compiled delegate for invocation, or <c>null</c> if no valid method is found.</returns>
        /// <exception cref="MultipleInjectMethodsException">Thrown if more than one method marked for injection is found at the same level in the type hierarchy.</exception>
        private static InjectMethod BuildInjectMethod(Type type)
        {
            MethodInfo found = null;
            int countAtLevel = 0;
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (MethodInfo m in t.GetMethods(MemberFlags))
                {
                    if (!m.IsDefined(typeof(InjectAttribute), inherit: false)) continue;
                    found = m;
                    countAtLevel++;
                }

                if (found == null) continue;

                // Derived-level [Inject] wins; do not also pull from base. If the derived
                // level declared more than one, that's a hard error.
                if (countAtLevel > 1) throw new MultipleInjectMethodsException(type, countAtLevel);
                break;
            }

            if (found == null) return null;

            ParameterInfo[] parameters = found.GetParameters();

            // (object instance, object[] args) => ((Concrete)instance).Method((P0)args[0], (P1)args[1], ...)
            ParameterExpression instParam = Expression.Parameter(typeof(object), "instance");
            ParameterExpression argsParam = Expression.Parameter(typeof(object[]), "args");
            UnaryExpression instCast = Expression.Convert(instParam, found.DeclaringType!);
            Expression[] argExpressions = new Expression[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                BinaryExpression indexAccess = Expression.ArrayIndex(argsParam, Expression.Constant(i));
                argExpressions[i] = Expression.Convert(indexAccess, parameters[i].ParameterType);
            }

            MethodCallExpression call = Expression.Call(instCast, found, argExpressions);
            Expression<Action<object, object[]>> lambda =
                Expression.Lambda<Action<object, object[]>>(call, instParam, argsParam);

            return new InjectMethod(found, parameters, lambda.Compile());
        }
    }
}