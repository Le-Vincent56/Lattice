using System.Runtime.CompilerServices;

// Lets the EditMode test assembly read internal types like Internal.InjectionPlanCache.CachedPlanCount
// for cache-hit assertions in MemberInjectionTests. Tests should not call into internals casually;
// reach for InternalsVisibleTo only when the property under test isn't expressible against the public API.
[assembly: InternalsVisibleTo("Didionysymus.Lattice.Tests")]