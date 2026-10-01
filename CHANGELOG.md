# Changelog

All notable changes to this package are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/), versioning per [SemVer](https://semver.org/).

## [Unreleased]

### Added
- `ChildSingletonRegistrationException`, thrown by `CreateChildScope` when a child registers a Singleton; carries `ServiceType` and `ImplType`. (minor)

### Changed
- A Scoped service registered in a parent scope is now constructed with that parent as its resolver. A child scope's registrations can no longer be captured by it, a dependency registered only in a child no longer satisfies it (the resolve throws `RegistrationNotFoundException`), and the `IDisposable` Transients created for it are disposed with the parent instead of the first child that asked. (major)
- `CreateChildScope` now validates the child against the creating scope as well as its ancestors; a cycle through the immediate parent is reported at creation instead of at first resolve. (major)
- `CreateChildScope` now rejects `Lifetime.Singleton` registrations in a child scope, including Singleton factories, open generics and closed generics preserved against a parent's open Singleton; use `Lifetime.Scoped` for one instance per child. `RegisterInstance` in a child is unaffected. (major)

### Fixed
- README's Lifetimes table said Scoped instances were cached in the resolving scope; they are cached in the scope that owns the registration, which is what the code and tests have always done. The captive-dependency rule is restated as implemented: a Singleton may not depend on a Scoped. (patch)

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
