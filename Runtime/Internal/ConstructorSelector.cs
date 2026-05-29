using System;
using System.Reflection;
using Didionysymus.Lattice.Runtime.Exceptions;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Selects the constructor to use for activation.
    /// Rules: single public constructor wins; if multiple, exactly one must be annotated with [Inject].
    /// </summary>
    internal static class ConstructorSelector
    {
        public static ConstructorInfo SelectConstructor(Type implType)
        {
            ConstructorInfo[] publicCtors = implType.GetConstructors();

            if (publicCtors.Length == 0)
                throw new DependencyResolutionException($"Type '{implType.FullName}' has no public constructor.");

            if (publicCtors.Length == 1) return publicCtors[0];

            // Multiple public constructors — exactly one must have [Inject]
            ConstructorInfo injectAnnotated = null;
            int injectCount = 0;

            for (int i = 0; i < publicCtors.Length; i++)
            {
                if (!publicCtors[i].IsDefined(typeof(InjectAttribute), inherit: false)) continue;
            
                injectAnnotated = publicCtors[i];
                injectCount++;
            }

            if (injectCount == 1) return injectAnnotated;

            throw new MultipleConstructorsException(implType, publicCtors.Length);
        }
    }
}