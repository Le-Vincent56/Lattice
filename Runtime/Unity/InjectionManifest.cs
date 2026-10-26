using System;
using System.Collections.Generic;
using UnityEngine;

namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// Records, for one walk unit, the components a prepare binds and initializes, in order, and the scope hosts the
    /// unit holds, so the hosts and the extensions read direct references instead of walking. Generated, never edited
    /// by hand; binds, initializes and destroys nothing itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where it is read: a scene manifest sits on a scene host's GameObject (a <see cref="LifetimeScopeBehaviour"/>, a
    /// subclass, or an <see cref="ExplicitScopeHost"/>) and describes that host's scene batch; a hierarchy manifest sits
    /// on a GameObject used as a walk root (a root given to <c>PrepareAsync</c>, the root of <c>InjectGameObject</c> or
    /// <c>InitializeGameObject</c>, a clone of <c>InstantiateAndInject</c>) and describes the walk of that root. A batch
    /// reads only the manifest of its own coverage on its unit's GameObject; one of the other coverage is ignored and
    /// the unit walked, and a unit without a manifest is walked as before. A manifest that was never generated, or
    /// whose format or coverage cannot be read, is refused by whichever unit finds it. A disabled manifest, or one on
    /// an inactive GameObject, still counts.
    /// </para>
    /// <para>
    /// Validity is checked on every use, without a walk: each listed component must be live (not destroyed, not a
    /// missing script), still have its list's role, be inside the coverage (in the host's scene, or the root or beneath
    /// it) and not sit inside a <see cref="ScopeBoundary"/> the unit excludes. An unusable manifest is refused with
    /// <see cref="InjectionManifestException"/> before anything is bound and before ownership is checked, unless
    /// <see cref="AllowDevelopmentFallback"/> applies.
    /// </para>
    /// <para>
    /// What is prepared is exactly what the lists name: a component added, or an object created, after generation is
    /// not prepared by the manifest, even when it exists before the prepare; regenerate, or bind and initialize it
    /// explicitly. The recorded hosts take the walk's place in the one-owner check.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class InjectionManifest : MonoBehaviour
    {
        /// <summary>
        /// The serialized format this version of Lattice writes and reads.
        /// </summary>
        internal const int CurrentFormat = 1;

        // 0 until generated; the format generation wrote.
        [SerializeField, HideInInspector] private int _format;

        // The walk unit described, as the int value of ManifestCoverage.
        [SerializeField, HideInInspector] private int _coverage;

        // The components to bind, in walk order.
        [SerializeField, HideInInspector] private MonoBehaviour[] _inject = Array.Empty<MonoBehaviour>();

        // The IInitializable components to initialize after the scope's pass, in walk order.
        [SerializeField, HideInInspector] private MonoBehaviour[] _initialize = Array.Empty<MonoBehaviour>();

        // Reserved for component startup; always written empty and read by nothing.
        [SerializeField, HideInInspector] private MonoBehaviour[] _asyncStart = Array.Empty<MonoBehaviour>();

        // Every scope host the generating walk passed, in walk order.
        [SerializeField, HideInInspector] private MonoBehaviour[] _hosts = Array.Empty<MonoBehaviour>();

        /// <summary>
        /// When true, and only where <see cref="Debug.isDebugBuild"/> is true (the Editor and development players), an
        /// unusable manifest is logged as one warning naming its problems and its unit is walked as if it had no
        /// manifest, instead of being refused with <see cref="InjectionManifestException"/>. Release players ignore it.
        /// </summary>
        /// <value>
        /// False by default, and reset to false on every play-mode entry and player start, so it never carries over from
        /// an earlier session. Set it on the main thread, from code that runs each play session, before preparing.
        /// </value>
        public static bool AllowDevelopmentFallback { get; set; }

        /// <summary>
        /// The serialized format: 0 when never generated, otherwise the format generation wrote.
        /// </summary>
        internal int Format => _format;

        /// <summary>
        /// The walk unit described; any stored value is returned, a corrupt one included.
        /// </summary>
        internal ManifestCoverage Coverage => (ManifestCoverage)_coverage;

        /// <summary>
        /// The components to bind, in walk order.
        /// </summary>
        internal IReadOnlyList<MonoBehaviour> Inject => _inject;

        /// <summary>
        /// The <see cref="IInitializable"/> components to initialize after the scope's pass, in walk order.
        /// </summary>
        internal IReadOnlyList<MonoBehaviour> Initialize => _initialize;

        /// <summary>
        /// Reserved for component startup; always empty, read by nothing.
        /// </summary>
        internal IReadOnlyList<MonoBehaviour> AsyncStart => _asyncStart;

        /// <summary>
        /// The scope hosts the generating walk passed, in walk order, the unit's own host included.
        /// </summary>
        internal IReadOnlyList<MonoBehaviour> Hosts => _hosts;

        /// <summary>
        /// Replaces the content with copies of <paramref name="targets"/>' two lists and of <paramref name="hosts"/>,
        /// an empty async list, <paramref name="coverage"/> and the current format.
        /// </summary>
        /// <param name="coverage">The walk unit the content describes.</param>
        /// <param name="targets">The walk's roles; read, never kept.</param>
        /// <param name="hosts">The hosts the walk passed, in walk order; read, never kept.</param>
        internal void SetContent(ManifestCoverage coverage, InjectionTargets targets, IReadOnlyList<MonoBehaviour> hosts)
        {
            _inject = targets.Inject.ToArray();
            _initialize = targets.Initialize.ToArray();
            _asyncStart = Array.Empty<MonoBehaviour>();

            MonoBehaviour[] recordedHosts = new MonoBehaviour[hosts.Count];
            for (int i = 0; i < recordedHosts.Length; i++)
            {
                recordedHosts[i] = hosts[i];
            }

            _hosts = recordedHosts;
            _coverage = (int)coverage;
            _format = CurrentFormat;
        }

        /// <summary>
        /// Overwrites the stored format alone, so a test can make a manifest of a format this version does not read.
        /// </summary>
        /// <param name="format">The format to store.</param>
        internal void SetFormat(int format)
        {
            _format = format;
        }

        /// <summary>
        /// Clears <see cref="AllowDevelopmentFallback"/>. Unity runs it on the main thread at subsystem registration, on
        /// every play-mode entry and player start, before any scene loads, with or without a domain reload.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetFallback() => AllowDevelopmentFallback = false;
    }
}