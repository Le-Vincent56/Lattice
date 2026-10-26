using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// Gathers a batch's components into <see cref="InjectionTargets"/>, by the rules both hosts and both extensions
    /// share: only <see cref="MonoBehaviour"/>s, inactive ones included; the inject role for a type with injection
    /// members and the initialize role for an <see cref="IInitializable"/>; null (missing-script) and destroyed entries
    /// skipped; a <see cref="ScopeBoundary"/> subtree skipped unless its GameObject is a root given to the call, and
    /// always on a scene root of a scene batch. A unit whose GameObject carries a usable <see cref="InjectionManifest"/>
    /// of its coverage is read from the manifest; any other unit is walked once. An unusable manifest is refused with
    /// <see cref="InjectionManifestException"/> before anything else, unless the development fallback walks its unit
    /// instead. For a host's batch it also refuses a second owner with <see cref="ScopeOwnershipException"/> before
    /// anything is bound. <see cref="Generate"/> fills a manifest by the same rules.
    /// </summary>
    /// <remarks>
    /// Main thread only: the pools and the type cache are unsynchronized statics, and every entry point checks the
    /// thread before it calls in. Targets, walk buffers and sets come from pools and are cleared before they return, so
    /// a nested walk gets its own and no scene reference outlives the call. Each list keeps batch order, then
    /// <c>GetComponentsInChildren</c> order per root; that order is not a contract.
    /// </remarks>
    internal static class ComponentDiscovery
    {
        // A batch holds more than one scope host outside boundaries; {0} is every host, the preparing one first.
        internal const string HostConflictFormat = "More than one scope host owns these objects: {0}. Keep one scope host per scene, or put a ScopeBoundary on the root of each subtree another scope prepares.";

        // A given root lies beneath another given root; {0} is the inner root, {1} its given ancestor.
        private const string OverlapBeneathFormat = "The given roots overlap: {0} is beneath {1}. Give each root once.";

        // A root is given more than once; {0} is the root.
        private const string OverlapTwiceFormat = "The given roots overlap: {0} is given twice. Give each root once.";

        // An automatic host sits on or beneath a boundary; {0} is the host.
        private const string NestedHostFormat = "A LifetimeScopeBehaviour cannot sit inside a ScopeBoundary: {0}. Prepare the boundary's root with an ExplicitScopeHost or with InjectGameObject and InitializeGameObject.";
        
        // A manifest cannot be used; {0} is the manifest's GameObject, {1} its problems joined with "; ".
        private const string ManifestUnusableFormat = "The InjectionManifest on {0} cannot be used: {1}. Rebuild the injection manifest, or remove the InjectionManifest component so the hierarchy is walked.";

        // A manifest-level problem: the manifest still has format 0.
        private const string NeverGeneratedProblem = "it was never generated";

        // A manifest-level problem; {0} is the stored format, {1} the current format.
        private const string NewerFormatProblem = "its format {0} is newer than this version of Lattice reads ({1})";

        // A manifest-level problem: an older or negative format, or an unknown coverage; {0} and {1} are the raw values.
        private const string InvalidDataProblem = "its data is not valid (format {0}, coverage {1})";

        // An entry is destroyed, null or a missing-script stand-in; {0} is the list, {1} the index.
        private const string MissingEntryProblem = "{0}[{1}] is missing (destroyed, or its script is missing)";

        // An inject entry's type has no injection members; {0} is the list, {1} the index, {2} the type's name.
        private const string NoInjectMembersProblem = "{0}[{1}] ({2}) has no [Inject] members";

        // An initialize entry's type is not an IInitializable; {0} is the list, {1} the index, {2} the type's name.
        private const string NotInitializableProblem = "{0}[{1}] ({2}) is not an IInitializable";

        // A scene manifest's entry is in another scene; {0} is the list, {1} the index, {2} the entry, {3} the host's scene.
        private const string OutsideSceneProblem = "{0}[{1}] ({2}) is not in scene '{3}'";

        // A hierarchy manifest's entry is not the root or beneath it; {0} is the list, {1} the index, {2} the entry, {3} the root.
        private const string OutsideRootProblem = "{0}[{1}] ({2}) is not beneath {3}";

        // An entry sits inside a boundary its unit excludes; {0} is the list, {1} the index, {2} the entry, {3} the nearest boundary.
        private const string InsideBoundaryProblem = "{0}[{1}] ({2}) is inside the ScopeBoundary on {3}";

        // Appended to an unusable manifest's line when the development fallback walks its unit instead.
        private const string FallbackSuffix = " Walked the hierarchy instead, because InjectionManifest.AllowDevelopmentFallback is set.";
        
        // Returned targets, cleared; Rent pops one or creates one.
        private static readonly Stack<InjectionTargets> _targetsPool = new Stack<InjectionTargets>();

        // Returned walk buffers, cleared; one per walk in progress, so a nested walk never shares the outer one.
        private static readonly Stack<List<MonoBehaviour>> _bufferPool = new Stack<List<MonoBehaviour>>();

        // Returned transform sets, cleared; rented when a walk meets a boundary below its root, and by the overlap check.
        private static readonly Stack<HashSet<Transform>> _setPool = new Stack<HashSet<Transform>>();

        // Targets, buffers and sets rented and not yet returned.
        private static int _outstanding;
        
        // Walks run since the domain loaded; read as a before/after delta by tests.
        private static int _walks;

        /// <summary>
        /// Rents empty targets from the pool, creating them when the pool is empty, and counts them outstanding until
        /// they are returned.
        /// </summary>
        /// <returns>Empty targets the caller owns until it passes them to <see cref="Return"/>, in a <c>finally</c>.</returns>
        internal static InjectionTargets Rent()
        {
            InjectionTargets targets = _targetsPool.Count > 0 ? _targetsPool.Pop() : new InjectionTargets();
            _outstanding++;
            return targets;
        }

        /// <summary>
        /// Clears <paramref name="targets"/>, returns them to the pool and counts them no longer outstanding.
        /// </summary>
        /// <param name="targets">Targets rented with <see cref="Rent"/>; the caller must not use them afterwards.</param>
        internal static void Return(InjectionTargets targets)
        {
            targets.Clear();
            _targetsPool.Push(targets);
            _outstanding--;
        }

        /// <summary>
        /// Collects a host's batch into <paramref name="targets"/>. First, without walking, it finds the batch's
        /// manifests (for a scene batch the scene <see cref="InjectionManifest"/> on <paramref name="preparingHost"/>'s
        /// GameObject; for given roots the hierarchy manifest on each live root) and validates each; every unusable one
        /// is refused, unless the development fallback logs it as one warning and walks its unit instead. Then it fills
        /// the targets unit by unit: a usable manifest's two lists, in order, or one walk of the root, skipping a
        /// boundary on a root of a scene batch and every boundary subtree beneath a root, adding each remaining
        /// component's roles. On the way it records every ownership violation: <paramref name="preparingHost"/> being a
        /// <see cref="LifetimeScopeBehaviour"/> inside a boundary; for given roots, a root given twice or beneath
        /// another; and any host other than <paramref name="preparingHost"/> that a walk left after boundary exclusion
        /// or a used manifest recorded and is still live. When any was recorded it throws one exception after the last
        /// unit, before the caller binds anything. A unit read from a manifest is not walked and rents no walk buffer.
        /// </summary>
        /// <param name="roots">The batch: the host scene's roots, or the roots given to the call; null and destroyed entries are skipped.</param>
        /// <param name="batch">Where <paramref name="roots"/> came from, which decides boundary claims, the overlap check and the manifest coverage read.</param>
        /// <param name="preparingHost">The host preparing the batch; excluded from the host check by reference.</param>
        /// <param name="targets">Rented targets the batch is appended to; returned by the caller whatever happens.</param>
        /// <exception cref="InjectionManifestException">
        /// Thrown before anything is walked and before ownership is reported, when a manifest the batch reads cannot be
        /// used and the development fallback does not apply; the message has one line per unusable manifest, in root
        /// order.
        /// </exception>
        /// <exception cref="ScopeOwnershipException">
        /// Thrown, after the batch is collected, when it holds another host outside a boundary, the given roots overlap,
        /// or a <see cref="LifetimeScopeBehaviour"/> prepares from inside a boundary; the message lists every violation.
        /// </exception>
        internal static void CollectBatch(
            IReadOnlyList<GameObject> roots,
            DiscoveryBatch batch,
            MonoBehaviour preparingHost,
            InjectionTargets targets
        )
        {
            // An automatic host is always its scene's host, so one on or beneath a boundary is a violation.
            string nestedHost = null;
            if (preparingHost is LifetimeScopeBehaviour && preparingHost.GetComponentInParent<ScopeBoundary>(true))
                nestedHost = string.Format(NestedHostFormat, Describe(preparingHost.gameObject));

            List<string> overlaps = batch == DiscoveryBatch.GivenRoots ? FindOverlaps(roots) : null;

            // Every manifest is found and validated before anything is walked: an unusable one is reported first, because
            // the ownership it records cannot be trusted.
            List<string> unusable = null;
            InjectionManifest sceneManifest = null;
            InjectionManifest[] rootManifests = null;
            if (batch == DiscoveryBatch.SceneRoots)
            {
                sceneManifest = ResolveManifest(preparingHost.gameObject, ManifestCoverage.Scene, ref unusable);
            }
            else
            {
                for (int i = 0; i < roots.Count; i++)
                {
                    GameObject root = roots[i];
                    if (!root) continue;

                    InjectionManifest manifest = ResolveManifest(root, ManifestCoverage.Hierarchy, ref unusable);
                    if (!manifest) continue;

                    rootManifests ??= new InjectionManifest[roots.Count];
                    rootManifests[i] = manifest;
                }
            }

            if (unusable != null) throw new InjectionManifestException(string.Join("\n", unusable));

            List<MonoBehaviour> otherHosts = null;
            List<MonoBehaviour> buffer = null;
            try
            {
                if (sceneManifest)
                {
                    AddFromManifest(sceneManifest, targets);
                    otherHosts = AddRecordedHosts(sceneManifest, preparingHost, otherHosts);
                }
                else
                {
                    for (int i = 0; i < roots.Count; i++)
                    {
                        GameObject root = roots[i];
                        if (!root) continue;

                        InjectionManifest manifest = rootManifests?[i];
                        if (manifest)
                        {
                            AddFromManifest(manifest, targets);
                            otherHosts = AddRecordedHosts(manifest, preparingHost, otherHosts);
                            continue;
                        }

                        // A scene batch never claims a boundary on a scene root; a given root claims one on itself.
                        if (batch == DiscoveryBatch.SceneRoots && root.TryGetComponent(out ScopeBoundary _)) continue;

                        // Rented on the first walk, so a batch read wholly from manifests rents no buffer.
                        buffer ??= RentBuffer();
                        Walk(root, buffer);
                        otherHosts = AddOtherHosts(buffer, preparingHost, otherHosts);
                        AddRoles(buffer, targets);
                    }
                }

                if (nestedHost != null || otherHosts != null || overlaps != null)
                    throw new ScopeOwnershipException(BuildOwnershipMessage(nestedHost, preparingHost, otherHosts, overlaps));
            }
            finally
            {
                if (buffer != null) ReturnBuffer(buffer);
            }
        }
        
        /// <summary>
        /// Collects one root for an extension into <paramref name="targets"/>: a null or destroyed root adds nothing; a
        /// root carrying a usable hierarchy <see cref="InjectionManifest"/> adds its two lists, in order, without
        /// walking; otherwise one walk over it, the root claiming a boundary on itself and every boundary subtree
        /// beneath it skipped. An unusable manifest on the root is refused before anything is added, unless the
        /// development fallback logs it as one warning and walks the root instead; a scene manifest of a usable format
        /// is ignored. Hosts are ordinary components here; no ownership is checked.
        /// </summary>
        /// <param name="root">The root passed to the extension.</param>
        /// <param name="targets">Rented targets the root's components are appended to.</param>
        /// <exception cref="InjectionManifestException">Thrown before anything is added when the manifest on <paramref name="root"/> cannot be used and the development fallback does not apply.</exception>
        internal static void CollectRoot(GameObject root, InjectionTargets targets)
        {
            if (!root) return;

            List<string> unusable = null;
            InjectionManifest manifest = ResolveManifest(root, ManifestCoverage.Hierarchy, ref unusable);
            if (unusable != null) throw new InjectionManifestException(unusable[0]);

            if (manifest)
            {
                AddFromManifest(manifest, targets);
                return;
            }

            List<MonoBehaviour> buffer = RentBuffer();
            try
            {
                Walk(root, buffer);
                AddRoles(buffer, targets);
            }
            finally
            {
                ReturnBuffer(buffer);
            }
        }
        
        /// <summary>
        /// Fills <paramref name="manifest"/> by discovery's rules for one walk unit: <see cref="ManifestCoverage.Scene"/>
        /// walks the roots of the manifest's scene as a scene batch, skipping a boundary on a scene root;
        /// <see cref="ManifestCoverage.Hierarchy"/> walks the manifest's GameObject as a given root, which claims a
        /// boundary on itself. Writes the inject and initialize roles in walk order, every host the walk passes, an
        /// empty async list and the current format. Never refuses for ownership.
        /// </summary>
        /// <param name="manifest">The manifest to fill; its GameObject is the root for hierarchy coverage, and its scene is the batch for scene coverage.</param>
        /// <param name="coverage">The walk unit to describe.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="coverage"/> is neither <see cref="ManifestCoverage.Scene"/> nor <see cref="ManifestCoverage.Hierarchy"/>.</exception>
        /// <exception cref="ArgumentException">Thrown by Unity, for scene coverage, when the manifest's GameObject is not in a loaded scene; the manifest is left unchanged.</exception>
        internal static void Generate(InjectionManifest manifest, ManifestCoverage coverage)
        {
            if (coverage != ManifestCoverage.Scene && coverage != ManifestCoverage.Hierarchy)
                throw new ArgumentOutOfRangeException(nameof(coverage), coverage, "Generate writes Scene or Hierarchy coverage only.");

            // A scene batch never claims a boundary on a scene root; a given root claims one on itself.
            bool sceneBatch = coverage == ManifestCoverage.Scene;
            GameObject[] roots = sceneBatch ? manifest.gameObject.scene.GetRootGameObjects() : new[] { manifest.gameObject };
            List<MonoBehaviour> hosts = new List<MonoBehaviour>();
            InjectionTargets targets = Rent();
            List<MonoBehaviour> buffer = RentBuffer();
            try
            {
                for (int i = 0; i < roots.Length; i++)
                {
                    GameObject root = roots[i];
                    if (!root) continue;

                    if (sceneBatch && root.TryGetComponent(out ScopeBoundary _)) continue;

                    Walk(root, buffer);
                    AddRoles(buffer, targets);
                    for (int j = 0; j < buffer.Count; j++)
                    {
                        if (IsHost(buffer[j])) hosts.Add(buffer[j]);
                    }
                }

                manifest.SetContent(coverage, targets, hosts);
            }
            finally
            {
                ReturnBuffer(buffer);
                Return(targets);
            }
        }
        
        /// <summary>
        /// Appends each entry of <paramref name="components"/> to <see cref="InjectionTargets.Inject"/> when its type
        /// has the inject role and to <see cref="InjectionTargets.Initialize"/> when it has the initialize role, in
        /// order; null (missing-script) and destroyed entries are skipped.
        /// </summary>
        /// <param name="components">One walk's components.</param>
        /// <param name="targets">The targets to append to.</param>
        internal static void AddRoles(IReadOnlyList<MonoBehaviour> components, InjectionTargets targets)
        {
            for (int i = 0; i < components.Count; i++)
            {
                MonoBehaviour component = components[i];
                if (!component) continue;

                ComponentRoles roles = ComponentTypeCache.GetRoles(component.GetType());
                if ((roles & ComponentRoles.Inject) != 0) targets.Inject.Add(component);

                if ((roles & ComponentRoles.Initialize) != 0) targets.Initialize.Add(component);
            }
        }

        /// <summary>
        /// Collects every host component beneath <paramref name="sceneRoots"/> into <paramref name="hosts"/>, in walk
        /// order: <see cref="LifetimeScopeBehaviour"/>s and <see cref="ExplicitScopeHost"/>s, active or not, enabled or
        /// not, skipping a boundary on a scene root and every boundary subtree beneath a root, as a scene batch does.
        /// </summary>
        /// <param name="sceneRoots">A scene's root GameObjects; null and destroyed entries are skipped.</param>
        /// <param name="hosts">The list the hosts are appended to.</param>
        internal static void CollectHosts(IReadOnlyList<GameObject> sceneRoots, List<MonoBehaviour> hosts)
        {
            List<MonoBehaviour> buffer = RentBuffer();
            try
            {
                for (int i = 0; i < sceneRoots.Count; i++)
                {
                    GameObject root = sceneRoots[i];
                    if (!root || root.TryGetComponent(out ScopeBoundary _)) continue;

                    Walk(root, buffer);
                    for (int j = 0; j < buffer.Count; j++)
                    {
                        if (IsHost(buffer[j])) hosts.Add(buffer[j]);
                    }
                }
            }
            finally
            {
                ReturnBuffer(buffer);
            }
        }

        /// <summary>
        /// Counts the entries held by every pooled collection: the pooled targets' two lists, the walk buffers and the
        /// sets. Zero when everything returned was cleared. For tests.
        /// </summary>
        /// <returns>The number of references the pools hold.</returns>
        internal static int CountPooledReferences()
        {
            int count = 0;
            foreach (InjectionTargets targets in _targetsPool)
            {
                count += targets.Inject.Count + targets.Initialize.Count;
            }

            foreach (List<MonoBehaviour> buffer in _bufferPool)
            {
                count += buffer.Count;
            }

            foreach (HashSet<Transform> set in _setPool)
            {
                count += set.Count;
            }

            return count;
        }

        /// <summary>
        /// Counts the targets, walk buffers and sets rented and not yet returned. Zero between calls. For tests.
        /// </summary>
        /// <returns>The number of outstanding rentals.</returns>
        internal static int CountOutstandingRentals() => _outstanding;
        
        /// <summary>
        /// Counts the walks run since the domain loaded. For tests, as a before/after delta.
        /// </summary>
        /// <returns>The number of walks run.</returns>
        internal static int CountWalks() => _walks;

        /// <summary>
        /// Names <paramref name="gameObject"/> for an ownership message as <c>{scene}/{names from the root down}</c>, so
        /// objects in different scenes stay distinct.
        /// </summary>
        /// <param name="gameObject">The object to name.</param>
        /// <returns>The scene's name and the hierarchy path, joined by <c>/</c>.</returns>
        internal static string Describe(GameObject gameObject)
        {
            string path = gameObject.name;
            for (Transform parent = gameObject.transform.parent; parent != null; parent = parent.parent)
            {
                path = parent.name + "/" + path;
            }

            return gameObject.scene.name + "/" + path;
        }

        /// <summary>
        /// Names each component's GameObject with <see cref="Describe"/>, in order, joined by <c>"; "</c>, for
        /// <see cref="HostConflictFormat"/>.
        /// </summary>
        /// <param name="components">The components to name.</param>
        /// <returns>The joined names.</returns>
        internal static string DescribeAll(IReadOnlyList<MonoBehaviour> components)
        {
            string[] names = new string[components.Count];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = Describe(components[i].gameObject);
            }

            return string.Join("; ", names);
        }

        /// <summary>
        /// Fills <paramref name="buffer"/> with one <c>GetComponentsInChildren</c> walk over <paramref name="root"/>,
        /// inactive objects included, then drops every boundary subtree beneath the root.
        /// </summary>
        /// <param name="root">A live root.</param>
        /// <param name="buffer">A rented buffer; cleared first.</param>
        private static void Walk(GameObject root, List<MonoBehaviour> buffer)
        {
            _walks++;
            buffer.Clear();
            root.GetComponentsInChildren(true, buffer);
            ExcludeBoundaries(root, buffer);
        }
        
        /// <summary>
        /// Drops from <paramref name="buffer"/> every live component on or beneath a GameObject that carries a
        /// <see cref="ScopeBoundary"/> below <paramref name="root"/>, keeping the order of the rest. A boundary on the
        /// root itself is claimed and excludes nothing. Rents no set when the buffer holds no such boundary; otherwise
        /// one parent walk per component, so the cost is components times depth whatever the number of boundaries.
        /// Null and destroyed entries are kept for <see cref="AddRoles"/> to skip.
        /// </summary>
        /// <param name="root">The walk's root.</param>
        /// <param name="buffer">The walk's components.</param>
        private static void ExcludeBoundaries(GameObject root, List<MonoBehaviour> buffer)
        {
            HashSet<Transform> boundaries = null;
            for (int i = 0; i < buffer.Count; i++)
            {
                MonoBehaviour component = buffer[i];
                if (!component) continue;

                if ((ComponentTypeCache.GetRoles(component.GetType()) & ComponentRoles.Boundary) == 0) continue;

                if (component.gameObject == root) continue;

                boundaries ??= RentSet();
                boundaries.Add(component.transform);
            }

            if (boundaries == null) return;

            try
            {
                Transform rootTransform = root.transform;
                int kept = 0;
                for (int i = 0; i < buffer.Count; i++)
                {
                    MonoBehaviour component = buffer[i];
                    if (component && IsInBoundary(component.transform, rootTransform, boundaries)) continue;

                    buffer[kept] = component;
                    kept++;
                }

                buffer.RemoveRange(kept, buffer.Count - kept);
            }
            finally
            {
                ReturnSet(boundaries);
            }
        }

        /// <summary>
        /// Returns whether <paramref name="transform"/> or one of its ancestors below <paramref name="rootTransform"/>
        /// is in <paramref name="boundaries"/>.
        /// </summary>
        /// <param name="transform">A component's transform, beneath the root or the root itself.</param>
        /// <param name="rootTransform">The walk's root, where the parent walk stops.</param>
        /// <param name="boundaries">The transforms of the boundaries below the root.</param>
        /// <returns>True when the component sits on or beneath a boundary below the root.</returns>
        private static bool IsInBoundary(Transform transform, Transform rootTransform, HashSet<Transform> boundaries)
        {
            for (Transform current = transform; current != null && current != rootTransform; current = current.parent)
            {
                if (boundaries.Contains(current)) return true;
            }

            return false;
        }

        /// <summary>
        /// Returns whether <paramref name="component"/> is live and has the host role, active or not, enabled or not.
        /// </summary>
        /// <param name="component">A walk entry; may be null or destroyed.</param>
        /// <returns>True for a live <see cref="LifetimeScopeBehaviour"/> or <see cref="ExplicitScopeHost"/>.</returns>
        private static bool IsHost(MonoBehaviour component)
        {
            return component && (ComponentTypeCache.GetRoles(component.GetType()) & ComponentRoles.Host) != 0;
        }

        /// <summary>
        /// Appends every host in <paramref name="buffer"/> other than <paramref name="preparingHost"/> (compared by
        /// reference) to <paramref name="otherHosts"/>, once each, creating the list on the first one.
        /// </summary>
        /// <param name="buffer">One root's walk, boundaries already excluded.</param>
        /// <param name="preparingHost">The host preparing the batch.</param>
        /// <param name="otherHosts">The hosts found so far; null while none was found.</param>
        /// <returns><paramref name="otherHosts"/>, or a new list when it was null and a host was found; null otherwise.</returns>
        private static List<MonoBehaviour> AddOtherHosts(List<MonoBehaviour> buffer, MonoBehaviour preparingHost, List<MonoBehaviour> otherHosts)
        {
            for (int i = 0; i < buffer.Count; i++)
            {
                MonoBehaviour component = buffer[i];
                if (!IsHost(component) || ReferenceEquals(component, preparingHost)) continue;

                otherHosts ??= new List<MonoBehaviour>();
                if (!otherHosts.Contains(component)) otherHosts.Add(component);
            }

            return otherHosts;
        }

        /// <summary>
        /// Checks given roots for overlap through one pooled set of their transforms and one parent walk per root: a
        /// root given more than once yields one "given twice" line, at its second occurrence; a root beneath another
        /// given root yields one "beneath" line naming its nearest given ancestor. Null and destroyed roots are skipped
        /// first and never overlap. Lines keep root order.
        /// </summary>
        /// <param name="roots">The given roots.</param>
        /// <returns>The overlap lines, or null when the roots do not overlap.</returns>
        private static List<string> FindOverlaps(IReadOnlyList<GameObject> roots)
        {
            HashSet<Transform> given = RentSet();
            try
            {
                bool repeated = false;
                for (int i = 0; i < roots.Count; i++)
                {
                    GameObject root = roots[i];
                    if (root && !given.Add(root.transform)) repeated = true;
                }

                List<string> overlaps = null;
                for (int i = 0; i < roots.Count; i++)
                {
                    GameObject root = roots[i];
                    if (!root) continue;

                    // Only a repeat makes an occurrence count matter; without one every root is its first occurrence.
                    int earlier = repeated ? CountEarlierOccurrences(roots, i) : 0;
                    string line = null;
                    if (earlier == 1)
                    {
                        line = string.Format(OverlapTwiceFormat, Describe(root));
                    }
                    else if (earlier == 0)
                    {
                        Transform ancestor = FindGivenAncestor(root.transform, given);
                        if (ancestor != null) line = string.Format(OverlapBeneathFormat, Describe(root), Describe(ancestor.gameObject));
                    }

                    if (line == null) continue;

                    overlaps ??= new List<string>();
                    overlaps.Add(line);
                }

                return overlaps;
            }
            finally
            {
                ReturnSet(given);
            }
        }

        /// <summary>
        /// Counts how often <paramref name="roots"/>[<paramref name="index"/>] appears before <paramref name="index"/>.
        /// </summary>
        /// <param name="roots">The given roots.</param>
        /// <param name="index">The position of a live root.</param>
        /// <returns>The number of earlier entries that are the same GameObject.</returns>
        private static int CountEarlierOccurrences(IReadOnlyList<GameObject> roots, int index)
        {
            int count = 0;
            for (int i = 0; i < index; i++)
            {
                if (ReferenceEquals(roots[i], roots[index])) count++;
            }

            return count;
        }

        /// <summary>
        /// Returns the nearest ancestor of <paramref name="transform"/>, its own transform excluded, that is in
        /// <paramref name="given"/>.
        /// </summary>
        /// <param name="transform">A given root's transform.</param>
        /// <param name="given">The transforms of every live given root.</param>
        /// <returns>The nearest given ancestor, or null when there is none.</returns>
        private static Transform FindGivenAncestor(Transform transform, HashSet<Transform> given)
        {
            for (Transform ancestor = transform.parent; ancestor != null; ancestor = ancestor.parent)
            {
                if (given.Contains(ancestor)) return ancestor;
            }

            return null;
        }

        /// <summary>
        /// Joins the recorded violations into one message, one line each, in the order nested host, host conflict
        /// (the preparing host first, then the others in walk order), overlaps in root order.
        /// </summary>
        /// <param name="nestedHost">The nested-host line, or null.</param>
        /// <param name="preparingHost">The host preparing the batch.</param>
        /// <param name="otherHosts">The other hosts found, or null.</param>
        /// <param name="overlaps">The overlap lines, or null.</param>
        /// <returns>The lines joined by <c>"\n"</c>.</returns>
        private static string BuildOwnershipMessage(
            string nestedHost,
            MonoBehaviour preparingHost,
            List<MonoBehaviour> otherHosts,
            List<string> overlaps
        )
        {
            List<string> lines = new List<string>();
            if (nestedHost != null) lines.Add(nestedHost);

            if (otherHosts != null)
            {
                List<MonoBehaviour> hosts = new List<MonoBehaviour>(otherHosts.Count + 1) { preparingHost };
                hosts.AddRange(otherHosts);
                lines.Add(string.Format(HostConflictFormat, DescribeAll(hosts)));
            }

            if (overlaps != null) lines.AddRange(overlaps);

            return string.Join("\n", lines);
        }
        
        /// <summary>
        /// Finds the manifest <paramref name="unit"/> reads for <paramref name="coverage"/> and validates it. A usable one
        /// is returned. An unusable one returns null: its line is appended to <paramref name="unusable"/>, or, when the
        /// development fallback applies, logged as one warning with the manifest as context, so the unit is walked.
        /// </summary>
        /// <param name="unit">The unit's GameObject, live: the preparing host's for a scene batch, the root's otherwise.</param>
        /// <param name="coverage">The coverage the unit reads.</param>
        /// <param name="unusable">The refusal lines so far; null until the first, created then.</param>
        /// <returns>The usable manifest, or null when there is none or it is unusable.</returns>
        private static InjectionManifest ResolveManifest(GameObject unit, ManifestCoverage coverage, ref List<string> unusable)
        {
            InjectionManifest manifest = FindManifest(unit, coverage);
            if (!manifest) return null;

            string line = Validate(manifest, coverage, unit, unit.scene);
            if (line == null) return manifest;

            if (IsFallbackAllowed())
            {
                Debug.LogWarning(line + FallbackSuffix, manifest);
                return null;
            }

            unusable ??= new List<string>();
            unusable.Add(line);
            return null;
        }

        /// <summary>
        /// Returns the <see cref="InjectionManifest"/> on <paramref name="unit"/>, enabled or not, active or not, unless it
        /// is of the current format with the other valid coverage, which the unit ignores. The format is checked before
        /// the coverage, so a manifest whose format or coverage cannot be read is returned to every unit that finds it.
        /// </summary>
        /// <param name="unit">The unit's GameObject, live.</param>
        /// <param name="coverage">The coverage the unit reads.</param>
        /// <returns>The manifest to validate, or null when there is none or it is ignored.</returns>
        private static InjectionManifest FindManifest(GameObject unit, ManifestCoverage coverage)
        {
            if (!unit.TryGetComponent(out InjectionManifest manifest)) return null;

            ManifestCoverage other = coverage == ManifestCoverage.Scene ? ManifestCoverage.Hierarchy : ManifestCoverage.Scene;
            if (manifest.Format == InjectionManifest.CurrentFormat && manifest.Coverage == other) return null;

            return manifest;
        }

        /// <summary>
        /// Checks <paramref name="manifest"/> without walking: a manifest-level problem alone (never generated, a newer
        /// format, or data that cannot be read), otherwise each <c>Inject</c> entry, then each <c>Initialize</c> entry, by
        /// index, each reporting its first failing check.
        /// </summary>
        /// <param name="manifest">A manifest <see cref="FindManifest"/> returned.</param>
        /// <param name="coverage">The coverage the unit reads.</param>
        /// <param name="unit">The unit's GameObject; the root a hierarchy manifest's entries must be beneath.</param>
        /// <param name="scene">The host's scene, which a scene manifest's entries must be in.</param>
        /// <returns>Null when the manifest is usable; otherwise its refusal line, naming the manifest, each problem and the fix.</returns>
        private static string Validate(
            InjectionManifest manifest,
            ManifestCoverage coverage,
            GameObject unit,
            Scene scene
        )
        {
            int format = manifest.Format;
            ManifestCoverage stored = manifest.Coverage;
            string manifestProblem = null;
            if (format == 0)
                manifestProblem = NeverGeneratedProblem;
            else if (format > InjectionManifest.CurrentFormat)
                manifestProblem = string.Format(NewerFormatProblem, format, InjectionManifest.CurrentFormat);
            else if (format != InjectionManifest.CurrentFormat || (stored != ManifestCoverage.Scene && stored != ManifestCoverage.Hierarchy))
                manifestProblem = string.Format(InvalidDataProblem, format, (int)stored);

            if (manifestProblem != null) return string.Format(ManifestUnusableFormat, Describe(manifest.gameObject), manifestProblem);

            List<string> problems = null;
            IReadOnlyList<MonoBehaviour> inject = manifest.Inject;
            for (int i = 0; i < inject.Count; i++)
            {
                string problem = FindEntryProblem(nameof(InjectionManifest.Inject), i, inject[i], ComponentRoles.Inject, coverage, unit, scene);
                if (problem == null) continue;

                problems ??= new List<string>();
                problems.Add(problem);
            }

            IReadOnlyList<MonoBehaviour> initialize = manifest.Initialize;
            for (int i = 0; i < initialize.Count; i++)
            {
                string problem = FindEntryProblem(nameof(InjectionManifest.Initialize), i, initialize[i], ComponentRoles.Initialize, coverage, unit, scene);
                if (problem == null) continue;

                problems ??= new List<string>();
                problems.Add(problem);
            }

            return problems == null ? null : string.Format(ManifestUnusableFormat, Describe(manifest.gameObject), string.Join("; ", problems));
        }

        /// <summary>
        /// Returns one entry's first failing check, in the order missing (destroyed, null, or a missing-script stand-in
        /// whose type is exactly <see cref="MonoBehaviour"/>), role, coverage, boundary.
        /// </summary>
        /// <param name="list">The list's name, <c>Inject</c> or <c>Initialize</c>.</param>
        /// <param name="index">The entry's index in the list.</param>
        /// <param name="entry">The entry; may be null or destroyed.</param>
        /// <param name="role">The role the list requires.</param>
        /// <param name="coverage">The coverage the unit reads.</param>
        /// <param name="unit">The unit's GameObject; the root a hierarchy manifest's entries must be beneath.</param>
        /// <param name="scene">The host's scene, which a scene manifest's entries must be in.</param>
        /// <returns>The entry's problem, or null when it passes every check.</returns>
        private static string FindEntryProblem(
            string list,
            int index,
            MonoBehaviour entry,
            ComponentRoles role,
            ManifestCoverage coverage,
            GameObject unit,
            Scene scene
        )
        {
            // A reference to a missing script reads as null or as a plain MonoBehaviour; both are missing.
            if (!entry || entry.GetType() == typeof(MonoBehaviour)) return string.Format(MissingEntryProblem, list, index);

            if ((ComponentTypeCache.GetRoles(entry.GetType()) & role) == 0)
            {
                string roleProblem = role == ComponentRoles.Inject ? NoInjectMembersProblem : NotInitializableProblem;
                return string.Format(roleProblem, list, index, entry.GetType().Name);
            }

            GameObject entryObject = entry.gameObject;
            if (coverage == ManifestCoverage.Scene && entryObject.scene != scene)
                return string.Format(OutsideSceneProblem, list, index, Describe(entryObject), scene.name);

            if (coverage == ManifestCoverage.Hierarchy && !entry.transform.IsChildOf(unit.transform))
                return string.Format(OutsideRootProblem, list, index, Describe(entryObject), Describe(unit));

            Transform boundary = FindExcludingBoundary(entry.transform, coverage, unit.transform);
            return !boundary ? null : string.Format(InsideBoundaryProblem, list, index, Describe(entryObject), Describe(boundary.gameObject));
        }

        /// <summary>
        /// Returns the nearest transform, from <paramref name="entry"/> itself upwards, that carries a
        /// <see cref="ScopeBoundary"/> the unit excludes: for scene coverage up to and including the scene root, because
        /// a scene batch excludes a boundary on a scene root; for hierarchy coverage stopping before
        /// <paramref name="unitRoot"/>, which claims its own boundary. One parent walk, no allocation.
        /// </summary>
        /// <param name="entry">A live entry's transform, inside the coverage.</param>
        /// <param name="coverage">The coverage the unit reads.</param>
        /// <param name="unitRoot">The unit's root transform; where a hierarchy walk stops.</param>
        /// <returns>The nearest excluding boundary's transform, or null when there is none.</returns>
        private static Transform FindExcludingBoundary(Transform entry, ManifestCoverage coverage, Transform unitRoot)
        {
            Transform stop = coverage == ManifestCoverage.Hierarchy ? unitRoot : null;
            for (Transform current = entry; current && current != stop; current = current.parent)
            {
                if (current.TryGetComponent(out ScopeBoundary _)) return current;
            }

            return null;
        }

        /// <summary>
        /// Appends a usable manifest's <c>Inject</c> and <c>Initialize</c> lists to <paramref name="targets"/>, in order.
        /// </summary>
        /// <param name="manifest">A manifest <see cref="Validate"/> accepted.</param>
        /// <param name="targets">The targets to append to.</param>
        private static void AddFromManifest(InjectionManifest manifest, InjectionTargets targets)
        {
            targets.Inject.AddRange(manifest.Inject);
            targets.Initialize.AddRange(manifest.Initialize);
        }

        /// <summary>
        /// Appends every live host recorded in <paramref name="manifest"/> other than <paramref name="preparingHost"/>
        /// (compared by reference) to <paramref name="otherHosts"/>, once each, creating the list on the first one; a
        /// null or destroyed entry is skipped, as a destroyed host is by a walk.
        /// </summary>
        /// <param name="manifest">A manifest the batch reads instead of walking.</param>
        /// <param name="preparingHost">The host preparing the batch.</param>
        /// <param name="otherHosts">The hosts found so far; null while none was found.</param>
        /// <returns><paramref name="otherHosts"/>, or a new list when it was null and a host was found; null otherwise.</returns>
        private static List<MonoBehaviour> AddRecordedHosts(InjectionManifest manifest, MonoBehaviour preparingHost, List<MonoBehaviour> otherHosts)
        {
            IReadOnlyList<MonoBehaviour> hosts = manifest.Hosts;
            for (int i = 0; i < hosts.Count; i++)
            {
                MonoBehaviour host = hosts[i];
                if (!IsHost(host) || ReferenceEquals(host, preparingHost)) continue;

                otherHosts ??= new List<MonoBehaviour>();
                if (!otherHosts.Contains(host)) otherHosts.Add(host);
            }

            return otherHosts;
        }

        /// <summary>
        /// Returns whether an unusable manifest is walked past instead of refused: only when
        /// <see cref="InjectionManifest.AllowDevelopmentFallback"/> is set and <see cref="Debug.isDebugBuild"/> is true
        /// (the Editor and development players), so a release player always refuses.
        /// </summary>
        /// <returns>True when the development fallback applies.</returns>
        private static bool IsFallbackAllowed() => InjectionManifest.AllowDevelopmentFallback && Debug.isDebugBuild;

        /// <summary>
        /// Rents an empty walk buffer, creating one when the pool is empty, and counts it outstanding.
        /// </summary>
        /// <returns>An empty buffer the caller returns with <see cref="ReturnBuffer"/>.</returns>
        private static List<MonoBehaviour> RentBuffer()
        {
            List<MonoBehaviour> buffer = _bufferPool.Count > 0 ? _bufferPool.Pop() : new List<MonoBehaviour>();
            _outstanding++;
            return buffer;
        }

        /// <summary>
        /// Clears <paramref name="buffer"/>, returns it to the pool and counts it no longer outstanding.
        /// </summary>
        /// <param name="buffer">A buffer rented with <see cref="RentBuffer"/>.</param>
        private static void ReturnBuffer(List<MonoBehaviour> buffer)
        {
            buffer.Clear();
            _bufferPool.Push(buffer);
            _outstanding--;
        }

        /// <summary>
        /// Rents an empty transform set, creating one when the pool is empty, and counts it outstanding.
        /// </summary>
        /// <returns>An empty set the caller returns with <see cref="ReturnSet"/>.</returns>
        private static HashSet<Transform> RentSet()
        {
            HashSet<Transform> set = _setPool.Count > 0 ? _setPool.Pop() : new HashSet<Transform>();
            _outstanding++;
            return set;
        }

        /// <summary>
        /// Clears <paramref name="set"/>, returns it to the pool and counts it no longer outstanding.
        /// </summary>
        /// <param name="set">A set rented with <see cref="RentSet"/>.</param>
        private static void ReturnSet(HashSet<Transform> set)
        {
            set.Clear();
            _setPool.Push(set);
            _outstanding--;
        }
    }
}