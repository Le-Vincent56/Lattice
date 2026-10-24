using System;

namespace Didionysymus.Lattice.Runtime.Exceptions
{
    /// <summary>
    /// Thrown when one participant's lifecycle hook throws during a scope's lifecycle pass. Names the participant's
    /// runtime type and the phase, and carries the hook's own exception as <see cref="Exception.InnerException"/>.
    /// The scope that ran the pass is faulted afterwards and refuses further passes; dispose it and build a new one.
    /// </summary>
    public sealed class LifecycleException : DependencyResolutionException
    {
        /// <summary>
        /// The runtime type of the participant whose hook threw.
        /// </summary>
        public Type ParticipantType { get; }

        /// <summary>
        /// The lifecycle phase that was running when the hook threw.
        /// </summary>
        public LifecyclePhase Phase { get; }

        public LifecycleException(Type participantType, LifecyclePhase phase, Exception inner)
            : base(BuildMessage(participantType, phase, inner), inner)
        {
            ParticipantType = participantType;
            Phase = phase;
        }

        /// <summary>
        /// Builds the message: the phase, the participant's full type name and the inner exception's message.
        /// </summary>
        /// <param name="participantType">The participant's runtime type.</param>
        /// <param name="phase">The phase that was running.</param>
        /// <param name="inner">The hook's exception.</param>
        /// <returns>The exception message.</returns>
        private static string BuildMessage(Type participantType, LifecyclePhase phase, Exception inner)
        {
            return $"{phase} failed for '{participantType.FullName}': {inner.Message}";
        }
    }
}