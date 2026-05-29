# Changelog

All notable changes to this package are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/), versioning per [SemVer](https://semver.org/).

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
