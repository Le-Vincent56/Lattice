using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Didionysymus.Lattice.Runtime.Exceptions;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Builds a Func delegate that reflectively invokes the selected constructor,
    /// resolving each parameter from the container. Step 2 replaces this with
    /// ExpressionActivatorFactory for IL2CPP-ready compiled expressions.
    /// </summary>
    internal sealed class ReflectionActivatorFactory
    {
        /// <summary>
        /// Builds a delegate that constructs an instance of the specified type using its constructor.
        /// The delegate resolves each constructor parameter from the provided dependency resolver,
        /// enabling dependency injection for object construction.
        /// </summary>
        /// <param name="implType">
        /// The type of the object to be instantiated. This type must have a public constructor, and
        /// if there are multiple constructors, one of them must be explicitly annotated with
        /// dependency injection attributes, if applicable.
        /// </param>
        /// <returns>
        /// A function that accepts an <see cref="IObjectResolver"/> instance and returns an
        /// instantiated object of the specified type, with all dependencies injected as resolved
        /// by the resolver.
        /// </returns>
        public Func<IObjectResolver, object> Build(Type implType)
        {
            ConstructorInfo ctor = ConstructorSelector.SelectConstructor(implType);
            ParameterInfo[] parameters = ctor.GetParameters();

            return resolver =>
            {
                object[] args = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    args[i] = ResolveParameter(resolver, parameters[i]);
                }

                return ctor.Invoke(args);
            };
        }

        /// <summary>
        /// Resolves the value for a specific parameter by using the provided object resolver.
        /// Supports dependency injection for various parameter types such as collections,
        /// arrays, and optional parameters with the <see cref="InjectAttribute"/>.
        /// </summary>
        /// <param name="resolver">
        /// The resolver instance used to resolve dependencies for the parameter. This
        /// instance provides methods to resolve individual objects or collections of objects.
        /// </param>
        /// <param name="parameter">
        /// Represents the metadata for the parameter to be resolved, including its type
        /// and any custom attributes applied to it.
        /// </param>
        /// <returns>
        /// The resolved object for the given parameter. If the parameter type is a collection
        /// or array, a collection of resolved objects is returned. If the parameter is marked
        /// as optional and no registration exists, this method returns the default value for
        /// the parameter type.
        /// </returns>
        private static object ResolveParameter(IObjectResolver resolver, ParameterInfo parameter)
        {
            Type parameterType = parameter.ParameterType;

            // Collection parameters resolve via ResolveAll<T>
            if (parameterType.IsGenericType)
            {
                Type genericDefinition = parameterType.GetGenericTypeDefinition();
                if (genericDefinition == typeof(IReadOnlyList<>)
                    || genericDefinition == typeof(IEnumerable<>)
                    || genericDefinition == typeof(IList<>)
                    || genericDefinition == typeof(List<>)
                   )
                {
                    Type elementType = parameterType.GetGenericArguments()[0];
                    MethodInfo resolveAll = typeof(IObjectResolver)
                        .GetMethod(nameof(IObjectResolver.ResolveAll))
                        ?.MakeGenericMethod(elementType);

                    if (resolveAll != null) return resolveAll.Invoke(resolver, null);
                }
            }

            // Array parameters also resolve via ResolveAll<T>, then convert to T[]
            if (parameterType.IsArray)
            {
                Type elementType = parameterType.GetElementType();
                MethodInfo resolveAll = typeof(IObjectResolver)
                    .GetMethod(nameof(IObjectResolver.ResolveAll))
                    ?.MakeGenericMethod(elementType);

                if (resolveAll != null)
                {
                    IEnumerable list = (IEnumerable)resolveAll.Invoke(resolver, null);

                    // Build a temporary list to get the count, then copy into a typed array
                    List<object> items = new List<object>();
                    foreach (object item in list)
                    {
                        items.Add(item);
                    }

                    if (elementType != null)
                    {
                        Array array = Array.CreateInstance(elementType, items.Count);
                        for (int i = 0; i < items.Count; i++)
                        {
                            array.SetValue(items[i], i);
                        }

                        return array;
                    }
                }
            }

            // Check if this parameter is marked as optional
            bool optional = parameter.IsDefined(typeof(InjectAttribute), false)
                            && parameter.GetCustomAttribute<InjectAttribute>().Optional;

            try
            {
                return resolver.Resolve(parameterType);
            }
            catch (RegistrationNotFoundException) when (optional)
            {
                // Optional parameter with no registration — return default value
                return parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
            }
        }
    }
}