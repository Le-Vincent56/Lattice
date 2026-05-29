using System;
using System.Collections.Generic;
using System.Text;

namespace Didionysymus.Lattice.Runtime.Exceptions
{
    /// <summary>
    /// Thrown when a cyclical dependency is detected in the dependency graph.
    /// </summary>
    public sealed class CyclicDependencyException : DependencyResolutionException
    {
        public IReadOnlyList<Type> CyclePath { get; }

        public CyclicDependencyException(IReadOnlyList<Type> cyclePath) : base(BuildMessage(cyclePath))
        {
            CyclePath = cyclePath;
        }

        /// <summary>
        /// Builds a message describing the cycle.
        /// </summary>
        /// <param name="cyclePath">The cycle to describe.</param>
        /// <returns>A message describing the cycle.</returns>
        private static string BuildMessage(IReadOnlyList<Type> cyclePath)
        {
            StringBuilder stringBuilder = new StringBuilder("Cyclic dependency detected:\n");
            for (int i = 0; i < cyclePath.Count; i++)
            {
                if (i > 0) stringBuilder.Append(" -> ");
                stringBuilder.Append(cyclePath[i].FullName);
            }

            return stringBuilder.ToString();
        }
    }
}