using System;

namespace Didionysymus.Lattice.Runtime
{
    /// <summary>
    /// Provides utility methods for working with generic types, including the creation of closed generic types
    /// from an open generic type definition and specified type arguments.
    /// </summary>
    public static class GenericTypes
    {
        /// <summary>
        /// Creates closed generic types by combining an open generic type definition with the provided type arguments.
        /// </summary>
        /// <param name="openType">The open generic type definition to be closed. Must be a generic type definition.</param>
        /// <param name="typeArgs">
        /// The type arguments used to close the open generic type. The number of type arguments must match
        /// the arity of the generic type, or be a multiple of it if generating multiple closed types.
        /// </param>
        /// <returns>An array of closed generic <see cref="Type"/> instances created by applying the type arguments to the open generic type.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the <paramref name="openType"/> parameter is null.</exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the <paramref name="typeArgs"/> parameter is null or empty, when the <paramref name="openType"/>
        /// is not an open generic type definition, or if the number of type arguments is not valid.
        /// </exception>
        public static Type[] Close(Type openType, params Type[] typeArgs)
        {
            if (openType == null) throw new ArgumentNullException(nameof(openType));
            if (typeArgs == null || typeArgs.Length == 0)
                throw new ArgumentException("At least one type argument is required.", nameof(typeArgs));
            if (!openType.IsGenericTypeDefinition)
                throw new ArgumentException($"Type '{openType.FullName}' is not an open generic definition.",
                    nameof(openType));

            int arity = openType.GetGenericArguments().Length;
            Type[] result = new Type[typeArgs.Length];

            if (arity == 1)
            {
                for (int i = 0; i < typeArgs.Length; i++)
                {
                    result[i] = openType.MakeGenericType(typeArgs[i]);
                }

                return result;
            }

            if (typeArgs.Length % arity != 0)
            {
                throw new ArgumentException(
                    $"Open type '{openType.FullName}' has arity {arity}; type-args length {typeArgs.Length} must be a multiple of arity.",
                    nameof(typeArgs)
                );
            }

            int closedCount = typeArgs.Length / arity;
            Type[] results = new Type[closedCount];
            for (int i = 0; i < closedCount; i++)
            {
                Type[] slice = new Type[arity];
                Array.Copy(typeArgs, i * arity, slice, 0, arity);
                results[i] = openType.MakeGenericType(slice);
            }

            return results;
        }
    }
}