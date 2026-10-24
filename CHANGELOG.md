# Changelog

All notable changes to this package are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/), versioning per [SemVer](https://semver.org/).

## [Unreleased]

### Added
- `ChildSingletonRegistrationException`, thrown by `CreateChildScope` when a child registers a Singleton; carries `ServiceType` and `ImplType`. (minor)
- `ResolverLifecycleExtensions.InitializeInstance(object)`: initializes an externally supplied `IInitializable` once per scope, by reference, through the same tracking as services; returns whether it ran. (minor)
- `LifecycleException` (a `DependencyResolutionException` with `ParticipantType` and `Phase`) and `LifecyclePhase`. (minor)

### Changed
- A Scoped service registered in a parent scope is now constructed with that parent as its resolver. A child scope's registrations can no longer be captured by it, a dependency registered only in a child no longer satisfies it (the resolve throws `RegistrationNotFoundException`), and the `IDisposable` Transients created for it are disposed with the parent instead of the first child that asked. (major)
- `CreateChildScope` now validates the child against the creating scope as well as its ancestors; a cycle through the immediate parent is reported at creation instead of at first resolve. (major)
- `CreateChildScope` now rejects `Lifetime.Singleton` registrations in a child scope, including Singleton factories, open generics and closed generics preserved against a parent's open Singleton; use `Lifetime.Scoped` for one instance per child. `RegisterInstance` in a child is unaffected. (major)
- Every `As<T>()`, `AsSelf()` and `AsImplementedInterfaces()` alias of a Singleton or Scoped registration now resolves to one shared implementation instance, as documented; previously each alias built its own. Decorators still apply per service type: an alias with a decorator chain returns that chain around the shared instance. (major)
- The inner instance of a decorated Singleton or Scoped service is now disposed with its scope and initialized by `RunInitializables`; previously only the outermost decorator was. A decorator must no longer forward `Initialize` or `Dispose` to its inner, or the inner receives each call twice. (major)
- `RunAsyncStartablesAsync` starts an instance once per call even when its registration is aliased under more than one `IAsyncStartable` type. (major)
- `RunInitializables` now builds its participants: every Singleton or Scoped registration of the calling scope whose implementation or decorator type implements `IInitializable` is materialized, in registration order, before any `Initialize` runs, and `Initialize` then runs in creation order (dependencies before dependents, independents in registration order). Previously only instances somebody had already resolved took part. Transients and `RegisterInstance` values do not participate; a factory participates by its service type. (major)
- A thrown `Initialize` now surfaces as `LifecycleException` naming the participant and phase, and faults the scope: later `RunInitializables` and `InitializeInstance` calls on it and on its children throw `InvalidOperationException`; `Resolve` and `Dispose` still work. An instance is recorded as initialized only after its `Initialize` returns. A concurrent or re-entrant pass throws `InvalidOperationException`. (major)
- `RunInitializables` on a child scope now requires the parent's pass to have completed and throws `InvalidOperationException` otherwise; each scope's pass covers its own registrations only. (major)
- `InitializeGameObject` initializes each component once per scope, through `InitializeInstance`; calling it twice no longer runs `Initialize` twice, and it throws before the scope's own pass. (major)
- `LifetimeScopeBehaviour.Awake` now runs the service pass before initializing scene components, so a component's `Initialize` observes initialized services. (major)

### Fixed
- README's Lifetimes table said Scoped instances were cached in the resolving scope; they are cached in the scope that owns the registration, which is what the code and tests have always done. The captive-dependency rule is restated as implemented: a Singleton may not depend on a Scoped. (patch)
- `RunInitializables` compares participants by reference; two distinct instances of a type with value equality are both initialized. (patch)

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
