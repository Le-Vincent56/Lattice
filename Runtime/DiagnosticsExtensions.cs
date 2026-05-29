using System;
using System.Collections.Generic;
using System.Text;
using Didionysymus.Lattice.Runtime.Internal;

namespace Didionysymus.Lattice.Runtime
{
    /// <summary>
    /// Public diagnostics extensions on <see cref="IObjectResolver"/>. Renders the scope tree
    /// and per-type resolution paths as human-readable text for runtime introspection.
    /// Both extensions downcast to the built-in <see cref="Scope"/>; passing a user-supplied test double
    /// throws <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <remarks>
    /// Output is deterministic for a given scope structure modulo per-process
    /// <see cref="Scope.ScopeID"/> values. Tests that compare dumps across runs
    /// should normalize <c>Scope#N</c> tokens via regex before comparing.
    /// </remarks>
    public static class DiagnosticsExtensions
    {
        // Column widths used by the dump format. Service-type names are padded to
        // ServiceNameWidth; impl names (in the single-impl case) to ImplNameWidth.
        // Multi-binding rows using ImplRowIndent to push impl names out past the
        // service-name column for readability
        private const int ServiceNameWidth = 36;
        private const int ImplNameWidth = 28;
        private const string ImplRowIndent = "\t";

        /// <summary>
        /// Walks the scope tree root-down from the resolver's containing scope and renders it as text.
        /// Each scope reports its <c>ScopeID</c>, parent ID, closed registrations in registration order (multi-bindings
        /// flatten into "N implementations:" plus per-implementation rows), and any decorator chains
        /// attached to the scope's local registry.
        /// </summary>
        /// <param name="resolver">The resolver. Must be the built-in <see cref="Scope"/>.</param>
        /// <returns>The rendered dump as a single string.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="resolver"/> is not the built-in <see cref="Scope"/> (e.g., a user-supplied
        /// test double). The dump relies on internal scope and registry surface that cannot be polyfilled through
        /// the public <see cref="IObjectResolver"/> contract.
        /// </exception>
        public static string DumpScopeTree(this IObjectResolver resolver)
        {
            if (resolver is not Scope scope)
            {
                throw new InvalidOperationException(
                    "DumpScopeTree is only supported on the built-in Scope resolver."
                );
            }

            // Walk up to the root so the dump always starts on the top of the tree
            // regardless of which scope the caller invoked it on.
            Scope root = scope;
            while (root.Parent != null) root = root.Parent;

            StringBuilder stringBuilder = new StringBuilder();
            DumpScopeRecursive(root, stringBuilder, indent: 0);
            return stringBuilder.ToString();
        }

        /// <summary>
        /// Walks from the resolver's scope upward through ancestor scopes and reports the first scope
        /// whose registry contains an explicit closed registration for <paramref name="type"/>. Only matches explicit
        /// closed entries in <see cref="Registry.ClosedRegistry"/>; open-generic promotion paths (resolved on demand by the runtime)
        /// are not surfaced.
        /// </summary>
        /// <param name="resolver">The resolver. Must be the built-in <see cref="Scope"/>.</param>
        /// <param name="type">The service type to look up.</param>
        /// <returns>
        /// A short report listing the matching scope (with hop count from the calling scope) and every registered implementation
        /// for the type, or a "no registration" message when the type is not registered anywhere on the ancestor chain.
        /// </returns>
        /// <exception cref="InvalidOperationException">Thrown when <paramref name="resolver"/> is not the built-in <see cref="Scope"/>.</exception>
        public static string DumpResolutionPath(this IObjectResolver resolver, Type type)
        {
            if (resolver is not Scope scope)
            {
                throw new InvalidOperationException(
                    "DumpResolutionPath is only supported on the built-in Scope resolver."
                );
            }

            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append("Resolution path for ")
                .Append(type.FullName)
                .AppendLine(":");

            // hop counts how many scope hops up from the calling scope the registration
            // was found at. hop = 0 means the calling scope itself.
            int hop = 0;
            for (Scope current = scope; current != null; current = current.Parent, hop++)
            {
                if (!current.Registry.ClosedRegistry.TryGetValue(type, out List<RegistrationEntry> entries) ||
                    entries.Count == 0)
                    continue;

                stringBuilder.Append(" found at scope#")
                    .Append(current.ScopeID)
                    .Append(" (hop = ")
                    .Append(hop)
                    .Append(")")
                    .AppendLine();

                for (int i = 0; i < entries.Count; i++)
                {
                    RegistrationEntry entry = entries[i];
                    stringBuilder.Append(" -> ")
                        .Append(entry.ImplType.FullName)
                        .Append(" (")
                        .Append(entry.Lifetime)
                        .Append(")")
                        .AppendLine();
                }

                return stringBuilder.ToString();
            }

            stringBuilder.AppendLine(" no registration found in this scope or any ancestor.");
            return stringBuilder.ToString();
        }

        /// <summary>
        /// Renders one scope into <paramref name="stringBuilder"/> at the requested indent level,
        /// then recurses into each child. Iterates <see cref="Registry.RegistrationOrder"/> so cross-service-type
        /// registration order is preserved; uses a per-scope set to skip the follow-up entries
        /// of an already-rendered multi-binding bucket.
        /// </summary>
        /// <param name="scope">The scope to dump.</param>
        /// <param name="stringBuilder">The buffer to render into.</param>
        /// <param name="indent">Tree depth; multiplied by 2 for spaces of indent.</param>
        private static void DumpScopeRecursive(Scope scope, StringBuilder stringBuilder, int indent)
        {
            string indentString = new string(' ', indent * 2);
            string parentLabel = scope.Parent == null
                ? "none"
                : $"#{scope.Parent.ScopeID}";
            string scopeLabel = scope.Parent == null
                ? "Root scope"
                : "Child scope";

            stringBuilder.Append(indentString)
                .Append("==")
                .Append(scopeLabel)
                .Append(" (Didionysymus.Lattice.Scope#")
                .Append(scope.ScopeID)
                .Append(", parent=")
                .Append(parentLabel)
                .Append(") ==")
                .AppendLine();

            // Walk RegistrationOrder so cross-service-type ordering is stable.
            // For multi-bindings, the first encountered entry triggers the full bucket render;
            // the remaining same-service-type entries are skipped
            // via seenEntries so we do not re-render the bucket per implementation.
            HashSet<RegistrationEntry> seenEntries = new HashSet<RegistrationEntry>();
            for (int i = 0; i < scope.Registry.RegistrationOrder.Count; i++)
            {
                RegistrationEntry entry = scope.Registry.RegistrationOrder[i];
                if (!seenEntries.Add(entry)) continue;

                List<RegistrationEntry> bucket = scope.Registry.ClosedRegistry[entry.ServiceType];
                int multiCount = bucket.Count;

                stringBuilder.Append(indentString).Append(" ");
                stringBuilder.Append(entry.ServiceType.Name.PadRight(ServiceNameWidth));

                if (multiCount > 1)
                {
                    stringBuilder.Append(" -> ")
                        .Append(multiCount)
                        .Append(" implementations:")
                        .AppendLine();

                    for (int j = 0; j < bucket.Count; j++)
                    {
                        RegistrationEntry implementation = bucket[j];

                        stringBuilder.Append(indentString)
                            .Append(ImplRowIndent)
                            .Append(implementation.ImplType.Name)
                            .Append(" (")
                            .Append(implementation.Lifetime)
                            .Append(")")
                            .AppendLine();

                        seenEntries.Add(implementation);
                    }
                }
                else
                {
                    stringBuilder.Append(" -> ")
                        .Append(entry.ImplType.Name.PadRight(ImplNameWidth))
                        .Append(" (")
                        .Append(entry.Lifetime)
                        .Append(")")
                        .AppendLine();
                }

                // Decorator chains hang off the same service type;
                // render right below the bucket they wrap so the relationship
                // is visible.
                if (scope.Registry.DecoratorChains.TryGetValue(entry.ServiceType, out List<DecoratorEntry> decorators))
                {
                    for (int k = 0; k < decorators.Count; k++)
                    {
                        DecoratorEntry decorator = decorators[k];
                        stringBuilder.Append(indentString)
                            .Append(" + decorator: ")
                            .Append(decorator.DecoratorType.Name)
                            .Append(" wraps ")
                            .Append(decorator.ServiceType.Name)
                            .AppendLine();
                    }
                }
            }

            IReadOnlyList<Scope> children = scope.Children;
            for (int i = 0; i < children.Count; i++)
            {
                stringBuilder.AppendLine();
                DumpScopeRecursive(children[i], stringBuilder, indent + 1);
            }
        }
    }
}