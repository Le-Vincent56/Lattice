using System.Runtime.CompilerServices;

// Lets the Unity editor assembly use the adapter's internal discovery types, so editor code applies the same
// eligibility, boundary and ownership rules as the runtime.
[assembly: InternalsVisibleTo("Didionysymus.Lattice.Unity.Editor")]

// Lets the PlayMode test assembly reach the discovery and manifest seams (ComponentDiscovery.Rent, Return, AddRoles,
// CollectBatch, CollectRoot, Generate, CountPooledReferences, CountOutstandingRentals, CountWalks, and the internal
// members of InjectionManifest) for missing-script, destroyed-entry, pool, walk-count and manifest-content assertions
// the public API cannot express. Tests should not call into internals casually.
[assembly: InternalsVisibleTo("Didionysymus.Lattice.Unity.Tests")]