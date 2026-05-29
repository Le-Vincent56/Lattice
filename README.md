# Lattice

A small, owned dependency-injection container for Unity. The core is pure C# with
no engine or third-party dependencies; a thin Unity adapter adds scene integration,
and an Editor assembly adds diagnostics. IL2CPP-safe by design.

- **Three lifetimes:** Transient, Scoped, Singleton.
- **Parent/child scope hierarchy** with parent-fallback resolution.
- **Constructor and member injection** (`[Inject]` fields, properties, methods).
- **Multi-binding** (`ResolveAll<T>()`, and `IReadOnlyList<T>` / `IEnumerable<T>` / `T[]` constructor params).
- **Decorators**, **open generics**, and lifecycle hooks (`IInitializable`, `IAsyncStartable`, `IDisposable`).
- **Cycle and captive-dependency detection** at registration time.
- **Text-dump diagnostics** plus an Editor menu item.

## Install

Add the package via its Git URL. In `Packages/manifest.json`:

```json
"com.didionysymus.lattice": "https://github.com/Le-Vincent56/Lattice.git#v1.0.0"
```

Or in the Editor: Window > Package Manager > Add package from git URL, then paste
`https://github.com/Le-Vincent56/Lattice.git#v1.x.x`.

To run the package's own tests in a consuming project, add it to `testables`:

```json
"testables": [ "com.didionysymus.lattice" ]
```

## Assemblies and namespaces

| Assembly | Namespace | Engine refs | Purpose |
|---|---|---|---|
| `Didionysymus.Lattice` | `Didionysymus.Lattice.Runtime` | none | Pure C# container core. Headlessly compilable. |
| `Didionysymus.Lattice.Unity` | `Didionysymus.Lattice.Runtime.Unity` | yes | Scene integration: `LifetimeScopeBehaviour`, `InjectGameObject`. |
| `Didionysymus.Lattice.Unity.Editor` | `Didionysymus.Lattice.Editor` | yes (Editor) | Diagnostics menu items. |

Note: the assembly names are flat (`Didionysymus.Lattice`), while the namespaces carry
the source folder (`...Runtime`, `...Runtime.Unity`). Import the namespace, reference the assembly.

## Quick start

```csharp
using Didionysymus.Lattice.Runtime;

public interface IGreeter
{
    string Greet(string name);
}

public sealed class ConsoleGreeter : IGreeter
{
    public string Greet(string name)
    {
        return $"Hello, {name}.";
    }
}

// Build a root container. The returned resolver is the root scope.
IObjectResolver resolver = Container.Build(builder =>
{
    builder.Register<IGreeter, ConsoleGreeter>(Lifetime.Singleton);
});

IGreeter greeter = resolver.Resolve<IGreeter>();

// Dispose the root to dispose every tracked IDisposable and all child scopes.
resolver.Dispose();
```

## Lifetimes

| Lifetime | Cached where | Disposed when |
|---|---|---|
| `Transient` | nowhere; new each resolve | when the resolving scope is disposed (only if `IDisposable`) |
| `Scoped` | the resolving scope | when that scope is disposed |
| `Singleton` | the root scope | when the root scope is disposed |

**Captive dependency rule** (enforced at registration time): a `Singleton` may only
depend on `Singleton`. A `Scoped` may depend on `Singleton` or `Scoped`. A `Transient`
may depend on anything. Violations throw `CaptiveDependencyException` with the full chain.

## Scopes

```csharp
IObjectResolver child = resolver.CreateChildScope(builder =>
{
    builder.Register<ISessionState, SessionState>(Lifetime.Scoped);
});
```

Child scopes inherit parent registrations; a child registration shadows the parent's.
Disposing a child does not dispose the parent; disposing the root cascades to all
undisposed children. Disposables are released in reverse creation order.

## Multi-binding

Register several implementations of one service; resolve all of them in registration
order. Resolution order is deterministic by registration order.

```csharp
builder.Register<IValidationRule, RangeRule>(Lifetime.Singleton);
builder.Register<IValidationRule, NotNullRule>(Lifetime.Singleton);

IReadOnlyList<IValidationRule> rules = resolver.ResolveAll<IValidationRule>();
// Resolve<IValidationRule>() returns the LAST registered (NotNullRule).
```

Constructor parameters typed `IReadOnlyList<T>`, `IEnumerable<T>`, or `T[]` are
auto-resolved via `ResolveAll<T>()`.

## Decorators

```csharp
builder.Register<IFoo, RealFoo>(Lifetime.Scoped);
builder.RegisterDecorator<IFoo, FooLogger>(Lifetime.Scoped);
builder.RegisterDecorator<IFoo, FooMetrics>(Lifetime.Scoped);

// Resolve<IFoo>() returns FooMetrics(FooLogger(RealFoo)).
// Last registered is outermost. Each decorator takes the wrapped service as its first ctor param.
```

## Open generics

```csharp
builder.RegisterOpenGeneric(typeof(IRepository<>), typeof(Repository<>), Lifetime.Scoped);

// Resolve<IRepository<Monster>>() closes Repository<Monster> on demand and caches it.
```

## Lifecycle hooks

```csharp
public sealed class CatalogLoader : IAsyncStartable, IDisposable
{
    public Task StartAsync(CancellationToken cancellationToken) { /* ... */ }
    public void Dispose() { /* ... */ }
}
```

- `IInitializable.Initialize()` runs once per instance, after all bindings in the scope complete.
- `IAsyncStartable.StartAsync()` runs in registration order, awaitable.
- `IDisposable.Dispose()` runs on scope disposal, reverse creation order.

Trigger the phases on the resolver:

```csharp
resolver.RunInitializables();
await resolver.RunAsyncStartablesAsync(cancellationToken);
```

## Unity integration

Subclass `LifetimeScopeBehaviour` on a scene GameObject and override `Configure`:

```csharp
using Didionysymus.Lattice.Runtime;
using Didionysymus.Lattice.Runtime.Unity;

public sealed class GameLifetimeScope : LifetimeScopeBehaviour
{
    protected override void Configure(IContainerBuilder builder)
    {
        builder.Register<IGreeter, ConsoleGreeter>(Lifetime.Singleton);
    }
}
```

On `Awake` (running at `[DefaultExecutionOrder(int.MinValue + 100)]`) the scope builds
its resolver, injects every MonoBehaviour in its scene, then runs Phase 2. If you
override `Awake`, call `base.Awake()` first or the scene walk and Phase 2 are skipped.

### Member injection and the two-phase contract

```csharp
public sealed class HudPresenter : MonoBehaviour, IInitializable
{
    private IGreeter _greeter;

    // Phase 1 (BIND ONLY): assign fields. Do not subscribe or touch other instances here.
    [Inject]
    public void Construct(IGreeter greeter)
    {
        _greeter = greeter;
    }

    // Phase 2 (SETUP): every instance in the scope is bound; cross-instance wiring is safe.
    public void Initialize()
    {
        Debug.Log(_greeter.Greet("HUD"));
    }
}
```

MonoBehaviour timing rules:

1. Do not read injected fields in `Awake()`; they are populated by `Start()`.
2. Put post-injection setup in `IInitializable.Initialize()`, which runs after Phase 1
   completes across the scene and before `Start()`.
3. Inject dynamically spawned prefabs explicitly, before their `Start()` runs:

```csharp
SpawnedThing instance = resolver.InstantiateAndInject(prefab);
// or, after a manual Instantiate:
resolver.InjectGameObject(spawnedGameObject);
```

## IL2CPP / AOT preservation (required for device builds)

Open-generic resolution uses `MakeGenericType`, which IL2CPP strips unless the closed
combination is preserved. This is a per-project responsibility. Use all three:

1. Declare closed combinations in an installer so they are pre-built and kept:

```csharp
builder.PreserveClosedGenerics(
    typeof(IRepository<Monster>),
    typeof(IRepository<Ability>)
);

// Ergonomic helper for one open generic with many type args:
builder.PreserveClosedGenerics(
    GenericTypes.Close(typeof(IRepository<>), typeof(Monster), typeof(Ability)));
```

2. Add a `link.xml` in your project preserving the assembly that holds the open-generic
   implementations.
3. Apply `[UnityEngine.Scripting.Preserve]` on individual implementation types as a fallback.

Forgetting this manifests as runtime exceptions on device that do not reproduce in the Editor.

## Diagnostics

```csharp
string tree = resolver.DumpScopeTree();
string path = resolver.DumpResolutionPath(typeof(IGreeter));
```

In the Editor: menu Lattice > Dump Active Scope Tree (Play mode only).

## Exceptions

All resolution failures derive from `Didionysymus.Lattice.Runtime.Exceptions.DependencyResolutionException`:
`RegistrationNotFoundException`, `CyclicDependencyException`, `CaptiveDependencyException`,
`MultipleConstructorsException`, `MultipleInjectMethodsException`. Every message includes the
full requested-type chain.

## Constructor selection

1. A single public constructor is used directly.
2. With multiple public constructors, the one annotated `[Inject]` is used; zero or more
   than one annotated throws at registration.
3. No public constructor throws at registration. Greediest-constructor-wins is not supported.

## Compatibility and contributing

- Targets Unity 6000.4.5f1 or newer, and compiles under Unity's default C# 9. The package uses block-scoped
  namespaces deliberately so it needs no project `csc.rsp` and no patched editor.
- `init`-only setters are supported via an in-package `IsExternalInit` polyfill.
- Namespaces are set explicitly (folder-aligned under `Didionysymus.Lattice`). Do not run an
  IDE "adjust namespaces to folder structure" pass against a project root, or it may re-fold
  paths into the namespace.
- Keep contributions inside the v1 cap (above). Anything beyond it (source-generated
  factories, contextual bindings, a live Editor graph window, async factories, custom-named
  scopes) is a deliberate future-version decision, not a casual addition.

## License

MIT License

Copyright (c) 2026 Vincent Le

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the “Software”), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
