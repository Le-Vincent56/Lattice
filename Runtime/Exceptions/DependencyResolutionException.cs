using System;

namespace Didionysymus.Lattice.Runtime.Exceptions
{
    /// <summary>
    /// Thrown when a dependency cannot be resolved.
    /// </summary>
    public class DependencyResolutionException : Exception
    {
        public DependencyResolutionException(string message) : base(message) { }
        public DependencyResolutionException(string message, Exception inner) : base(message, inner) { }
    }
}