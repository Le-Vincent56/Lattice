using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Didionysymus.Lattice.Runtime.Exceptions;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// IL2CPP-ready activator factory. Builds a delegate <c>Func&lt;IObjectResolver, object&gt;</c>
    /// for a target implementation type by emitting a <c>System.Linq.Expressions</c> tree shaped like:
    /// <code>
    /// (IObjectResolver resolver) => (object)new Impl(
    ///     ResolveOrAuto(resolver, paramType_0, optional_0),
    ///     ResolveOrAuto(resolver, paramType_1, optional_1),
    ///     ...
    /// </code>
    /// then compiling it via <see cref="Expression{TDelegate}.Compile()"/>. On Mono, the runtime
    /// JIT-compiles the resulting delegate; on IL2CPP it falls back to a tree-walking interpreter,
    /// which is still ~5-10x faster than reflection. The public Build signature matches <see cref="ReflectionActivatorFactory"/>
    /// s this type is a drop-in replacement.
    /// </summary>
    internal sealed class ExpressionActivatorFactory
    {
        /// <summary>
        /// Cached <see cref="MethodInfo"/> for the private static <see cref="ResolveOrAuto"/> helper.
        /// Resolved once at type-init via <see cref="nameof"/>; every <see cref="Build"/> call
        /// reuses it. If <see cref="ResolveOrAuto"/> is ever renamed without updating the <c>nameof</c> argument,
        /// this field will be null and the first <see cref="Build"/> invocation throws
        /// <see cref="NullReferenceException"/>.
        /// </summary>
        private static readonly MethodInfo _resolveOrAutoMethod =
            typeof(ExpressionActivatorFactory).GetMethod(nameof(ResolveOrAuto),
                BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>
        /// Builds a delegate that constructs an instance of <paramref name="implType"/> by resolving
        /// each constructor parameter from the supplied <see cref="IObjectResolver"/>. Constructor
        /// selection follows <see cref="ConstructorSelector"/> rules: a single public constructor wins,
        /// other exactly on constructor must carry <see cref="InjectAttribute"/>.
        /// </summary>
        /// <param name="implType">
        /// The concrete implementation type to instantiate. Must expose a public constructor selectable
        /// by <see cref="ConstructorSelector"/>; otherwise that selector throws.
        /// </param>
        /// <returns>
        /// A delegate that, when invoked with an <see cref="IObjectResolver"/>, returns a fully constructed instance
        /// of <paramref name="implType"/> with all dependencies injected.
        /// </returns>
        public Func<IObjectResolver, object> Build(Type implType)
        {
            ConstructorInfo constructor = ConstructorSelector.SelectConstructor(implType);
            ParameterInfo[] parameters = constructor.GetParameters();

            // The single resolver parameter that the compiled lambda accepts at runtime
            ParameterExpression resolverParam = Expression.Parameter(typeof(IObjectResolver), "resolver");

            Expression[] argExpressions = new Expression[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                argExpressions[i] = BuildParameterExpression(parameters[i], resolverParam);
            }

            // New Impl(arg0, arg1, ...) cast to object so the delegate's return type unifies for
            // value-type implementations (rare in DI; classes are the norm) and reference types alike
            NewExpression newExpression = Expression.New(constructor, argExpressions);
            UnaryExpression boxed = Expression.Convert(newExpression, typeof(object));
            Expression<Func<IObjectResolver, object>> lambda =
                Expression.Lambda<Func<IObjectResolver, object>>(boxed, resolverParam);
            return lambda.Compile();
        }

        /// <summary>
        /// Emits the sub-expression for one constructor parameter: it calls the static <see cref="ResolveOrAuto"/> helper
        /// with the parameter's runtime type and optional flag, then narrows the returned <see cref="object"/> back to
        /// the parameter's declared type via <see cref="Expression.Convert(Expression, Type)"/>. Mismatches surface as
        /// <see cref="InvalidCastException"/> at compiled-lambda invocation time, not at expression-build time.
        /// </summary>
        /// <param name="parameter">
        /// The <see cref="ParameterInfo"/> representing the constructor parameter to be resolved. Includes metadata
        /// such as the type of the parameter and any associated attributes, such as <see cref="InjectAttribute"/>.
        /// </param>
        /// <param name="resolverParam">
        /// A <see cref="ParameterExpression"/> representing the resolver parameter, which is an instance of
        /// <see cref="IObjectResolver"/> passed to the resolver method.
        /// </param>
        /// <returns>
        /// An <see cref="Expression"/> that, when executed, resolves the specified parameter either as a required or
        /// optional dependency and converts it to the required parameter type.
        /// </returns>
        private static Expression BuildParameterExpression(ParameterInfo parameter, ParameterExpression resolverParam)
        {
            Type parameterType = parameter.ParameterType;

            // Optional only matters when the parameter explicitly carries [Inject(Optional = true)]
            bool optional = parameter.IsDefined(typeof(InjectAttribute), false) &&
                            parameter.GetCustomAttribute<InjectAttribute>().Optional;

            ConstantExpression typeConst = Expression.Constant(parameterType, typeof(Type));
            ConstantExpression optionalConst = Expression.Constant(optional, typeof(bool));

            // ResolveOrAuto(resolver, parameterType, optional); single static call site per parameter
            MethodCallExpression resolved = Expression.Call(
                _resolveOrAutoMethod,
                resolverParam,
                typeConst,
                optionalConst
            );

            return Expression.Convert(resolved, parameterType);
        }

        /// <summary>
        /// Runtime-side parameter resolver invoked by the compiled lambda. Handles the auto-multi-bound
        /// shapes; <see cref="IReadOnlyList{T}"/>, <see cref="IList{T}"/>,
        /// <see cref="List{T}"/>, and <c>T[]</c>; by delegating to <see cref="IObjectResolver.ResolveAll{T}"/>
        /// via reflection-based generic method construction. All other parameter types fall through to a plain
        /// <see cref="IObjectResolver.Resolve(Type)"/> call. Optional parameters whose registration
        /// is missing yield a default value (null for reference types, the value-type default otherwise)
        /// rather than re-throwing <see cref="RegistrationNotFoundException"/>.
        /// </summary>
        /// <param name="resolver">
        /// The <see cref="IObjectResolver"/> responsible for resolving dependencies and constructing
        /// instances of the required types.
        /// </param>
        /// <param name="parameterType">
        /// The type of the parameter to resolve. Supports array types, generic collections, and
        /// individual dependency resolutions.
        /// </param>
        /// <param name="optional">
        /// Indicates whether resolution should tolerate missing registrations. If true, this allows
        /// fallback to default values or empty collections for unresolved types.
        /// </param>
        /// <returns>
        /// An object representing the resolved instance of <paramref name="parameterType"/>. For arrays or
        /// generic collections, this returns an appropriately populated collection. For single-instance
        /// resolutions, it returns a constructed instance of the required type.
        /// </returns>
        private static object ResolveOrAuto(IObjectResolver resolver, Type parameterType, bool optional)
        {
            // Array shape; resolve all, then convert to a typed T[] of the right lenght
            if (parameterType.IsArray)
            {
                Type elementType = parameterType.GetElementType();
                IList list = (IList)CallResolveAll(resolver, elementType);
                Array array = Array.CreateInstance(elementType, list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    array.SetValue(list[i], i);
                }

                return array;
            }

            // Generic collection shape: IReadOnlyList<T>, IEnumerable<T>, IList<T>, List<T>
            if (parameterType.IsGenericType)
            {
                Type definition = parameterType.GetGenericTypeDefinition();
                if (definition == typeof(IReadOnlyList<>)
                    || definition == typeof(IEnumerable<>)
                    || definition == typeof(IList<>)
                    || definition == typeof(List<>)
                   )
                {
                    Type elementType = parameterType.GetGenericArguments()[0];
                    return CallResolveAll(resolver, elementType);
                }
            }

            // Default path: single-instance resolve, with optional fallback for [Inject(Optional = true)]
            try
            {
                return resolver.Resolve(parameterType);
            }
            catch (RegistrationNotFoundException) when (optional)
            {
                return parameterType.IsValueType
                    ? Activator.CreateInstance(parameterType)
                    : null;
            }
        }

        /// <summary>
        /// Resolves all instances of the specified <paramref name="elementType"/> using
        /// the provided <paramref name="resolver"/> by invoking the <c>ResolveAll</c> method
        /// on the <see cref="IObjectResolver"/> for the specified type.
        /// </summary>
        /// <param name="resolver">
        /// The instance of <see cref="IObjectResolver"/> used to resolve instances of
        /// the specified <paramref name="elementType"/>.
        /// </param>
        /// <param name="elementType">
        /// The type of elements to resolve. This type is used to construct a generic
        /// invocation of the <c>ResolveAll</c> method on the <see cref="IObjectResolver"/>.
        /// </param>
        /// <returns>
        /// An object representing the resolved instances of the specified <paramref name="elementType"/>
        /// wrapped as an array or list, depending on the context in which this method is used.
        /// </returns>
        private static object CallResolveAll(IObjectResolver resolver, Type elementType)
        {
            MethodInfo resolveAll = typeof(IObjectResolver)
                .GetMethod(nameof(IObjectResolver.ResolveAll))
                ?.MakeGenericMethod(elementType);
            return resolveAll?.Invoke(resolver, null);
        }
    }
}