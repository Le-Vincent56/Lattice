# Changelog

All notable changes to this package are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/), versioning per [SemVer](https://semver.org/).

## [Unreleased]

### Added
- `ChildSingletonRegistrationException`, thrown by `CreateChildScope` when a child registers a Singleton; carries `ServiceType` and `ImplType`. (minor)
- `ResolverLifecycleExtensions.InitializeInstance(object)`: initializes an externally supplied `IInitializable` once per scope, by reference, through the same tracking as services; returns whether it ran. (minor)
- `LifecycleException` (a `DependencyResolutionException` with `ParticipantType` and `Phase`) and `LifecyclePhase`. (minor)
- `LifecyclePhase.Start`, reported by `LifecycleException` when a `StartAsync` fails. (minor)
- `ResolverLifecycleExtensions.ShutdownAsync(CancellationToken)`: stops lifecycle work on a scope and its descendants, cancels their participant tokens, waits for running startup to settle, then disposes the scope on the caller's context; the token bounds the wait, and disposal failures fault the task. (minor)
- `LifecycleState` is public, and `ResolverLifecycleExtensions.GetLifecycleState()` reads a scope's state (created, initializing, initialized, starting, ready, faulted, stopping, disposed) from any thread, a disposed scope included. A member may be added to the enum in a minor version. (minor)
- `ExplicitScopeHost`, a scene host a coordinator drives: `Attach(resolver, ScopeOwnership)`, `PrepareAsync` (binds a batch of roots, runs the scope's pass, initializes the batch's components, runs startup), `State` (`ScopeHostState`), `WaitUntilReadyAsync` for any number of callers, and `ShutdownAsync`. It never builds a resolver, becomes ready only when every step succeeded, and shuts an owned resolver down when it shuts down or is destroyed. (minor)

### Changed
- A Scoped service registered in a parent scope is now constructed with that parent as its resolver. A child scope's registrations can no longer be captured by it, a dependency registered only in a child no longer satisfies it (the resolve throws `RegistrationNotFoundException`), and the `IDisposable` Transients created for it are disposed with the parent instead of the first child that asked. (major)
- `CreateChildScope` now validates the child against the creating scope as well as its ancestors; a cycle through the immediate parent is reported at creation instead of at first resolve. (major)
- `CreateChildScope` now rejects `Lifetime.Singleton` registrations in a child scope, including Singleton factories, open generics and closed generics preserved against a parent's open Singleton; use `Lifetime.Scoped` for one instance per child. `RegisterInstance` in a child is unaffected. (major)
- Every `As<T>()`, `AsSelf()` and `AsImplementedInterfaces()` alias of a Singleton or Scoped registration now resolves to one shared implementation instance, as documented; previously each alias built its own. Decorators still apply per service type: an alias with a decorator chain returns that chain around the shared instance. (major)
- The inner instance of a decorated Singleton or Scoped service is now disposed with its scope and initialized by `RunInitializables`; previously only the outermost decorator was. A decorator must no longer forward `Initialize` or `Dispose` to its inner, or the inner receives each call twice. (major)
- `RunInitializables` now builds its participants: every Singleton or Scoped registration of the calling scope whose implementation or decorator type implements `IInitializable` is materialized, in registration order, before any `Initialize` runs, and `Initialize` then runs in creation order (dependencies before dependents, independents in registration order). Previously only instances somebody had already resolved took part. Transients and `RegisterInstance` values do not participate; a factory participates by its service type. (major)
- A thrown `Initialize` now surfaces as `LifecycleException` naming the participant and phase, and faults the scope: later `RunInitializables` and `InitializeInstance` calls on it and on its children throw `InvalidOperationException`; `Resolve` and `Dispose` still work. An instance is recorded as initialized only after its `Initialize` returns. A concurrent or re-entrant pass throws `InvalidOperationException`. (major)
- `RunInitializables` on a child scope now requires the parent's pass to have completed and throws `InvalidOperationException` otherwise; each scope's pass covers its own registrations only. (major)
- `InitializeGameObject` initializes each component once per scope, through `InitializeInstance`; calling it twice no longer runs `Initialize` twice, and it throws before the scope's own pass. (major)
- `LifetimeScopeBehaviour.Awake` now runs the service pass before initializing scene components, so a component's `Initialize` observes initialized services. (major)
- `RunAsyncStartablesAsync` now starts the `IAsyncStartable` instances the initialization pass built: `RunInitializables` builds every Singleton or Scoped registration whose implementation or decorator type implements `IInitializable` or `IAsyncStartable`, and startup starts every `IAsyncStartable` its scope has cached, once each by reference, without building anything. A startable registered under an unrelated service type now starts; Transient and `RegisterInstance` startables no longer start. Startup requires the scope's pass and, on a child, the parent's startup to have completed, and throws `InvalidOperationException` otherwise, and also when a cached `IInitializable` startable was created after the pass and is not yet initialized. Startable constructors now run in the pass, so a `LifetimeScopeBehaviour` constructs them in `Awake`. (major)
- `RunAsyncStartablesAsync` runs in creation order (dependencies first, independents in registration order across service types) instead of grouping by service type. (major)
- Startup runs once per scope: a call after it succeeded starts nothing, and a call while it runs, including from inside `StartAsync`, throws `InvalidOperationException`. Once startup has begun, `RunInitializables` initializes nothing: it returns when nothing is pending and throws `InvalidOperationException` naming an `IInitializable` created since; initialize such objects with `InitializeInstance`. (major)
- A thrown `StartAsync` now surfaces as `LifecycleException` with `Phase == LifecyclePhase.Start` and faults the scope, like a failed `Initialize`. (major)
- `RunAsyncStartablesAsync` checks its token before and between participants and stops scheduling on cancellation; cancellation after startup began faults the scope and completes the task as cancelled. Disposing a scope cancels its running startup through the token participants receive, and a disposed scope never completes startup. (major)
- `RunAsyncStartablesAsync` continues on the caller's synchronization context between participants; called from the Unity main thread, every `StartAsync` runs on the main thread. (major)
- `Dispose` now runs every cleanup step and then throws one `AggregateException` listing each failure, in the order it happened, when a child scope's disposal, a tracked object's `Dispose` or a token callback threw; previously each was swallowed. The scope is disposed either way, and a second `Dispose` does nothing. (major)
- The token startup participants receive now lives until the scope is disposed: `Dispose` and `ShutdownAsync` cancel it before anything is disposed, so work a participant started with it ends with the scope. Cancelling the caller's token after startup ended still does not reach it. (major)
- Every decorator in a Singleton or Scoped chain is now disposed, initialized and started, not only the outermost; a Transient's inner instance and each of its decorators are now disposed with the requesting scope. A decorator must not forward `Initialize`, `StartAsync` or `Dispose` to the object it wraps. (major)
- `RunInitializables` and `RunAsyncStartablesAsync` stop before the next participant when the scope is disposed during them; the pass then throws `ObjectDisposedException`. (major)
- `LifetimeScopeBehaviour.OnDestroy` logs each cleanup failure of a self-built resolver as its own console entry and does not throw. (major)
- `LifetimeScopeBehaviour.Resolver` is null after `OnDestroy`. (major)

### Fixed
- README's Lifetimes table said Scoped instances were cached in the resolving scope; they are cached in the scope that owns the registration, which is what the code and tests have always done. The captive-dependency rule is restated as implemented: a Singleton may not depend on a Scoped. (patch)
- `RunInitializables` compares participants by reference; two distinct instances of a type with value equality are both initialized. (patch)
- A faulted scope quotes its first fault in every later refusal, even when a second failure races with the first. (patch)
- A disposed scope no longer holds the objects it initialized, its root Singletons or its internal token and thread resources. A disposable Transient whose resolve completes after its scope's disposal began is disposed, and the resolve throws `ObjectDisposedException`; `CreateChildScope` on a parent disposed meanwhile throws `ObjectDisposedException` instead of returning a child nothing disposes. (patch)
- A child scope whose parent is faulted is refused with the parent's fault as inner exception and message; the message for an initializable created after the pass no longer says it was created after startup began. (patch)
- `LifetimeScopeBehaviour.SetResolver` called after `OnDestroy` still throws `InvalidOperationException`, now with a message that names a destroyed host. (patch)

## [1.0.2] - 2026-05-29
- Added LICENSE.md meta file to prevent Unity package import errors.

## [1.0.1] - 2026-05-29

- Raised the minimum supported Unity version to **6000.4.5f1** (`unity` `6000.4` + `unityRelease` `5f1`). The Editor diagnostics use the zero-argument `Object.FindObjectsByType<T>()` overload, which is only available from that release onward; earlier Unity 6 versions now get a clear compatibility message instead of a compile error.

## [1.0.0] - 2026-05-29

### Added

- Initial extraction from the Aethera: Rising project as a standalone package.
- Three lifetimes (Transient, Scoped, Singleton), parent/child scope hierarchy.
- Constructor and member injection; multi-binding; decorators; open generics.
- Lifecycle hooks (IInitializable, IAsyncStartable, IDisposable).
- Cycle and captive-dependency detection at registration time.
- Unity adapter (LifetimeScopeBehaviour, InjectGameObject) and Editor diagnostics.
