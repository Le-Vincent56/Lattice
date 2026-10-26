using System.Runtime.CompilerServices;

// Lets the EditMode test assembly read internal types like Internal.InjectionPlanCache.CachedPlanCount,
// GetPlan and HasInjectionMembers for the cache and metadata assertions in MemberInjectionTests and
// InjectionMetadataTests. Tests should not call into internals casually; reach for InternalsVisibleTo only
// when the property under test isn't expressible against the public API.
[assembly: InternalsVisibleTo("Didionysymus.Lattice.Tests")]

// Lets the Unity adapter and its editor assembly read Internal.InjectionPlanCache.HasInjectionMembers, to decide
// which components need injection. That one member is their whole allowed surface: they must not use any other
// core internal, and every reference from Runtime/Unity or Editor to Didionysymus.Lattice.Runtime.Internal is
// checked against this list at review.
[assembly: InternalsVisibleTo("Didionysymus.Lattice.Unity")]
[assembly: InternalsVisibleTo("Didionysymus.Lattice.Unity.Editor")]