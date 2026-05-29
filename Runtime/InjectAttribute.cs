using System;

namespace Didionysymus.Lattice.Runtime
{
    /// <summary>
    /// Marks a field, property, method or constructor parameter fpr injection.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Constructor)]
    public sealed class InjectAttribute : Attribute
    {
        public bool Optional { get; init; }
    }
}