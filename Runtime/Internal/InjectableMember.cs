using System;

namespace Didionysymus.Lattice.Runtime.Internal
{
    /// <summary>
    /// Pure-metadata description of a single <c>[Inject]</c>-annotated field or property
    /// discovered on a type. The <see cref="Setter"/> delegate is compiled by <see cref="InjectionPlanCache"/>
    /// via expression trees so member assignment in <c>Scope.Inject</c> avoids reflection at runtime.
    /// </summary>
    internal sealed class InjectableMember
    {
        /// <summary>
        /// The reflected member name (field or property). Surfaced for diagnostics only.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The declared type of the member; used as the resolution key against the active scope.
        /// </summary>
        public Type MemberType { get; }

        /// <summary>
        /// When <c>true</c>, missing registrations are tolerated: the member receives
        /// <c>default(MemberType)</c> instead of throwing <see cref="Exceptions.RegistrationNotFoundException"/>.
        /// Mirrors <see cref="InjectAttribute.Optional"/>.
        /// </summary>
        public bool Optional { get; }

        /// <summary>
        /// (instance, value) =&gt; ((Concrete)instance).Member = (MemberType)value.
        /// Compiled once per type per process by <see cref="InjectionPlanCache"/>.
        /// </summary>
        public Action<object, object> Setter { get; }

        public InjectableMember(string name, Type memberType, bool optional, Action<object, object> setter)
        {
            Name = name;
            MemberType = memberType;
            Optional = optional;
            Setter = setter;
        }
    }
}