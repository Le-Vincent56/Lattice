# Changelog

All notable changes to this package are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/), versioning per [SemVer](https://semver.org/).

## [1.0.0] - 2026-05-29

### Added

- Initial extraction from the Aethera: Rising project as a standalone package.
- Three lifetimes (Transient, Scoped, Singleton), parent/child scope hierarchy.
- Constructor and member injection; multi-binding; decorators; open generics.
- Lifecycle hooks (IInitializable, IAsyncStartable, IDisposable).
- Cycle and captive-dependency detection at registration time.
- Unity adapter (LifetimeScopeBehaviour, InjectGameObject) and Editor diagnostics.
