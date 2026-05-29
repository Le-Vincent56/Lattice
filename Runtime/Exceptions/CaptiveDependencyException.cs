using System;
using System.Collections.Generic;
using System.Text;

namespace Didionysymus.Lattice.Runtime.Exceptions
{
    public sealed class CaptiveDependencyException : DependencyResolutionException
    {
        public Type OuterType { get; }
        public Lifetime OuterLifetime { get; }
        public Type InnerType { get; }
        public Lifetime InnerLifetime { get; }
        public IReadOnlyList<Type> Chain { get; }

        public CaptiveDependencyException(
            Type outerType,
            Lifetime outerLifetime,
            Type innerType,
            Lifetime innerLifetime,
            IReadOnlyList<Type> chain
        ) : base(BuildMessage(outerType, outerLifetime, innerType, innerLifetime, chain))
        {
            OuterType = outerType;
            OuterLifetime = outerLifetime;
            InnerType = innerType;
            InnerLifetime = innerLifetime;
            Chain = chain;
        }

        /// <summary>
        /// Builds a detailed error message describing a captive dependency scenario,
        /// outlining the dependency chain and the conflicting lifetimes.
        /// </summary>
        /// <param name="outerType">The type of the outer dependency in the chain that has a longer lifetime.</param>
        /// <param name="outerLifetime">The lifetime associated with the outer dependency object.</param>
        /// <param name="innerType">The type of the inner dependency that has a shorter conflicting lifetime. </param>
        /// <param name="innerLifetime">The lifetime associated with the inner dependency object.</param>
        /// <param name="chain">The chain of dependency types leading to the conflict.</param>
        /// <returns>A formatted string describing the captive dependency issue.</returns>
        private static string BuildMessage(
            Type outerType,
            Lifetime outerLifetime,
            Type innerType,
            Lifetime innerLifetime,
            IReadOnlyList<Type> chain
        )
        {
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.AppendLine("Captive dependency detected:");
            for (int i = 0; i < chain.Count; i++)
            {
                stringBuilder.Append(' ', (i + 1) * 2);
                if (i > 0) stringBuilder.Append(" -> ");
                stringBuilder.Append(chain[i].FullName);
                stringBuilder.AppendLine();
            }

            stringBuilder.AppendFormat(
                "   {0} ({1}) cannot depend on {2} ({3}) - outer lifetime would capture inner instance",
                outerType.FullName,
                outerLifetime,
                innerType.FullName,
                innerLifetime
            );

            return stringBuilder.ToString();
        }
    }
}