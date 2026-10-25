using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Didionysymus.Lattice.Runtime.Unity
{
    /// <summary>
    /// The steps both scene hosts share: reading the root GameObjects of a host's scene, preparing a batch of roots
    /// against a resolver (bind, the scope's initialization pass, then the batch's components), logging cleanup
    /// failures that no caller can receive, and refusing a call off Unity's main thread.
    /// </summary>
    internal static class ScopeHostEngine
    {
        // The managed thread ID of Unity's main thread, recorded by RecordMainThread on every play-mode entry; 0 until
        // then, which lets every caller pass.
        private static int _mainThreadID;
         
        /// <summary>
        /// Returns the root GameObjects of the scene <paramref name="host"/> lives in, or an empty array when it is not
        /// yet attached to a valid scene (e.g., during certain editor-only construction pathways that fire <c>Awake</c>
        /// before scene assignment completes), so preparing the batch silently does nothing.
        /// </summary>
        /// <param name="host">The host's GameObject.</param>
        /// <returns>The scene's root GameObjects at the moment of the call.</returns>
        /// <exception cref="ArgumentException">Thrown by Unity when the scene is valid but not loaded.</exception>
        internal static GameObject[] GetSceneRoots(GameObject host)
        {
            UnityEngine.SceneManagement.Scene scene = host.scene;
            return scene.IsValid() ? scene.GetRootGameObjects() : Array.Empty<GameObject>();
        }

        /// <summary>
        /// Prepares a batch of root GameObjects against <paramref name="resolver"/>: binds every MonoBehaviour beneath
        /// each root, inactive ones included, runs the scope's initialization pass, then initializes the batch's
        /// <see cref="IInitializable"/> components once each. One batch for both walks, so nothing is initialized that
        /// was not bound. An object created during the pass is in neither walk; bind and initialize it explicitly with
        /// <c>InjectGameObject</c> and <c>InitializeGameObject</c>. Null and destroyed roots are skipped.
        /// </summary>
        /// <param name="resolver">The resolver the batch is bound to and initialized in.</param>
        /// <param name="roots">The batch, fixed by the caller.</param>
        /// <exception cref="Exceptions.DependencyResolutionException">Thrown when binding a component's <c>[Inject]</c> members fails, for example on a missing registration; later roots stay unbound and nothing is initialized.</exception>
        /// <exception cref="InvalidOperationException">Thrown when <paramref name="resolver"/> is not the built-in resolver, or its scope refuses the pass or a component's initialization (shutting down, faulted, its parent's pass not completed, or an initializable created since startup began).</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the scope has been disposed.</exception>
        /// <exception cref="Exceptions.LifecycleException">Thrown when a service's or a component's <c>Initialize</c> throws; the scope is then faulted.</exception>
        internal static void Prepare(IObjectResolver resolver, IReadOnlyList<GameObject> roots)
        {
            for (int i = 0; i < roots.Count; i++)
            {
                resolver.InjectGameObject(roots[i]);
            }

            resolver.RunInitializables();

            for (int i = 0; i < roots.Count; i++)
            {
                resolver.InitializeGameObject(roots[i]);
            }
        }

        /// <summary>
        /// Logs each inner exception of <paramref name="failure"/> as its own console entry, in order, so no failure
        /// hides behind another. For a failure raised in a Unity callback, or in work such a callback started, that no
        /// caller can receive.
        /// </summary>
        /// <param name="failure">The aggregate whose inner exceptions are logged.</param>
        /// <param name="context">The object the console selects for each entry; null for none.</param>
        internal static void LogFailures(AggregateException failure, UnityEngine.Object context)
        {
            for (int i = 0; i < failure.InnerExceptions.Count; i++)
            {
                Debug.LogException(failure.InnerExceptions[i], context);
            }
        }
        
        /// <summary>
        /// Refuses a call made off Unity's main thread, before it changes anything. Passes every caller until the main
        /// thread has been recorded.
        /// </summary>
        /// <param name="member">The name of the <see cref="ExplicitScopeHost"/> member being called.</param>
        /// <exception cref="InvalidOperationException">Thrown when the calling thread is not Unity's main thread.</exception>
        internal static void ThrowIfNotMainThread(string member)
        {
            if (_mainThreadID != 0 && Thread.CurrentThread.ManagedThreadId != _mainThreadID)
                throw new InvalidOperationException($"ExplicitScopeHost.{member} must be called on Unity's main thread.");
        }

        /// <summary>
        /// Records the main thread's managed thread ID. Unity runs it on the main thread at subsystem registration,
        /// on every play-mode entry and player start, before any scene loads.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RecordMainThread() =>  _mainThreadID = Thread.CurrentThread.ManagedThreadId;
    }
}