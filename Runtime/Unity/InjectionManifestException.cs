using System;

namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// Thrown before anything is bound when an <see cref="InjectionManifest"/> cannot be used: it was never generated,
    /// its format is newer, or an entry is missing, has lost its role, has left its scene or root, or now sits inside a
    /// <see cref="ScopeBoundary"/>. The message names each manifest as <c>{scene}/{hierarchy path}</c>, each problem
    /// and the fix.
    /// </summary>
    /// <remarks>
    /// <see cref="LifetimeScopeBehaviour"/> throws it from <c>Awake</c>; both <c>ExplicitScopeHost.PrepareAsync</c>
    /// overloads fault their task with it, change nothing and stay attached; <see cref="UnityResolverExtensions.InjectGameObject"/>
    /// and <see cref="UnityResolverExtensions.InitializeGameObject"/> throw it, and so does
    /// <c>InstantiateAndInject</c> for the clone's manifest.
    /// </remarks>
    public sealed class InjectionManifestException : InvalidOperationException
    {
        public InjectionManifestException(string message) : base(message) { }
    }
}