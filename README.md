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
"com.didionysymus.lattice": "https://github.com/Le-Vincent56/Lattice.git#vX.X.X"
```

Or in the Editor: Window > Package Manager > Add package from git URL, then paste
`https://github.com/Le-Vincent56/Lattice.git#vX.X.X`.

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

| Lifetime | Cached where | Constructed with | Disposed when (`IDisposable` only) |
|---|---|---|---|
| `Transient` | nowhere; new each resolve | the requesting scope | when the requesting scope is disposed |
| `Scoped` | the scope whose registration it is (the owning scope) | the owning scope | when the owning scope is disposed |
| `Singleton` | the root scope; registrable only at the root | the root scope | when the root scope is disposed |

A cached instance is built by the scope that caches it: its dependencies come from that scope's view of the
registry, and the `IDisposable` Transients created while building it belong to that scope. A child registration
never changes what a parent-owned instance is made of; it only shadows for resolves that start in the child. A
dependency that only a child registers does not satisfy a parent-owned service; resolving that service throws
`RegistrationNotFoundException`.

**Ownership.** A scope owns what it built: Singletons (the root), Scoped instances (the scope whose registration it
is), disposable Transients (the scope that resolved them), and every object of a decorator chain it built. The caller
owns what it supplied: `RegisterInstance` values, scene components, objects passed to `Inject` or
`InitializeInstance`. A scope never disposes, destroys or releases what the caller owns beyond dropping its own
references. To hand a supplied object to the container, register it through a factory:
`RegisterFactory(_ => instance, Lifetime.Singleton)` at the root, `Lifetime.Scoped` in a child.

**Captive dependency rule** (enforced at `Build`): a `Singleton` may not depend on a `Scoped`, directly or through
any chain of `Transient`s. Everything else is allowed. Violations throw `CaptiveDependencyException` with the full
chain.

## Scopes

```csharp
IObjectResolver child = resolver.CreateChildScope(builder =>
{
    builder.Register<ISessionState, SessionState>(Lifetime.Scoped);
});
```

Child scopes inherit parent registrations; a child registration shadows the parent's for `Resolve` (`ResolveAll`
returns parent and child registrations together, root first). A Scoped registration in a parent is one instance
for the parent and every descendant; a Scoped registration in a child is one instance per child, so siblings each
get their own. A child may not register a `Singleton`: `CreateChildScope` throws
`ChildSingletonRegistrationException`. Use `Lifetime.Scoped` for one instance per child; `RegisterInstance` is
allowed in any scope. `CreateChildScope` validates the child against the creating scope and every ancestor, so a
cycle that crosses the boundary is reported at creation. Disposing a child disposes only what the child owns and
does not affect the parent; disposing the root cascades to all undisposed children. Disposables are released in
reverse creation order.

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

### Aliases

`As<T>()`, `AsSelf()` and `AsImplementedInterfaces()` bind more service types to one registration. Every alias of a
`Singleton` or `Scoped` registration resolves the same instance. Decorators apply per service type: an alias with a
decorator chain returns that chain around the shared instance, and other aliases return the instance undecorated.
Two separate `Register` calls stay two registrations with two instances.

```csharp
builder.Register<AudioService>(Lifetime.Singleton).As<IAudioService>().As<IVolumeControl>();
// Resolve<AudioService>(), Resolve<IAudioService>() and Resolve<IVolumeControl>() return one AudioService.
```

## Decorators

```csharp
builder.Register<IFoo, RealFoo>(Lifetime.Scoped);
builder.RegisterDecorator<IFoo, FooLogger>(Lifetime.Scoped);
builder.RegisterDecorator<IFoo, FooMetrics>(Lifetime.Scoped);

// Resolve<IFoo>() returns FooMetrics(FooLogger(RealFoo)).
// Last registered is outermost. Each decorator takes the wrapped service as its first ctor param.
```

The container owns every object of a chain it builds. For a Singleton or Scoped service, the inner instance and every
decorator, not only the outermost, are initialized and started innermost first and disposed outermost first; for a
Transient, the inner instance and each disposable decorator are disposed with the requesting scope. A decorator must
not forward `Initialize`, `StartAsync` or `Dispose` to the object it wraps; forwarding would run each call twice.

## Open generics

```csharp
builder.RegisterOpenGeneric(typeof(IRepository<>), typeof(Repository<>), Lifetime.Scoped);

// Resolve<IRepository<Monster>>() closes Repository<Monster> on demand and caches it.
```

## Lifecycle hooks

```csharp
public sealed class CatalogLoader : IInitializable, IDisposable
{
    public void Initialize() { /* every dependency is constructed and initialized */ }
    public void Dispose() { /* ... */ }
}
```

- **Who participates.** `RunInitializables()` builds every `Singleton` or `Scoped` registration of its scope whose
  implementation type, or a decorator type registered for its service type, implements `IInitializable`, then calls
  `Initialize()` once per instance. `Transient` registrations never participate. A `RegisterInstance` value is
  caller-owned: the container neither initializes, starts nor disposes it; call `resolver.InitializeInstance(instance)`
  after the pass if it needs `Initialize`, or register it through `RegisterFactory(_ => instance, ...)` to make it the
  container's. A factory participates when its service type is `IInitializable`; otherwise
  register the concrete type and alias the interface: `RegisterFactory<Loader>(..., Lifetime.Singleton).As<ILoader>()`.
  Closed generics participate once preserved with `PreserveClosedGenerics`.
- **Order.** Every participant is constructed before any is initialized. `Initialize` runs in creation order: a
  dependency before anything constructed with it, independent participants in registration order, a decorated
  instance before its decorator.
- **Once, by reference.** A scope initializes an instance at most once across repeated passes and
  `InitializeInstance` calls, whichever registrations or aliases refer to it.
- **Per scope, parents first.** Each scope runs its own pass over its own registrations. `RunInitializables` on a
  child throws `InvalidOperationException` until the parent's pass has completed.
- **Lazy objects.** `Resolve` never calls `Initialize`. An `IInitializable` created after the pass (an opaque factory
  result, an unpreserved closed generic) is initialized by the next `RunInitializables` on its scope.
- **Failure.** A thrown `Initialize` surfaces as `LifecycleException` (participant type, phase, inner exception) and
  faults the scope: later passes and `InitializeInstance` calls on it and on its children throw
  `InvalidOperationException`. `Resolve` and `Dispose` still work. Dispose the scope and build a new one; the pass is
  not retried. A pass started while one is running, including from inside an `Initialize` body, throws
  `InvalidOperationException`.
- **Startup.** `RunAsyncStartablesAsync(token)` starts every `IAsyncStartable` the scope's pass built (Singleton or
  Scoped registrations whose implementation or decorator type implements it) plus any the scope already holds, once
  each by reference, in creation order, one at a time. It builds nothing, and requires the scope's pass and, on a
  child, the parent's startup to have completed. It runs once per scope: a later call starts nothing, a concurrent
  call throws `InvalidOperationException`. Transient and `RegisterInstance` startables are not started. Startable
  constructors run in the pass, so a `LifetimeScopeBehaviour` constructs them in `Awake`. Once startup has begun,
  `RunInitializables` initializes nothing: it returns when nothing is pending (a scene host handed a started scope
  keeps working) and throws naming an `IInitializable` created since; initialize such objects with
  `InitializeInstance`.
- **Startup failure and cancellation.** A thrown `StartAsync` surfaces as `LifecycleException` with
  `Phase == LifecyclePhase.Start` and faults the scope. The token is checked before and between participants;
  cancelling it after startup began, or disposing the scope, stops further participants, faults the scope and cancels
  the returned task. Participants should observe the token they receive; Lattice waits for a running participant and
  never abandons it. `Dispose` does not wait.
- **Participant token.** The token a participant receives is the scope's own. It is cancelled when the caller's token
  is cancelled during startup, or when the scope is shut down or disposed at any time until then; cancelling the
  caller's token after startup ended does not reach it. A participant may keep it for background work that should end
  with the scope; work that must outlive the scope needs a token of its own.
- **Startup context.** Startup continues on the caller's synchronization context between participants: call it from
  the Unity main thread and every `StartAsync` runs on the main thread. A caller without a context (a thread-pool
  thread) continues on the thread pool. Await it; never block on the task. This covers the calls Lattice makes: code
  inside a participant after its own `ConfigureAwait(false)` is the participant's responsibility. A participant that
  completes asynchronously resumes startup no earlier than the next frame.
- **Dispose.** `Dispose` cancels the participant token, disposes child scopes (each its own subtree first), then the
  scope's own disposables in reverse order of creation; disposing a child never touches its parent. Every step runs
  whatever an earlier one threw, and `Dispose` then throws one `AggregateException` listing each failure in the order
  it happened; the scope is disposed either way. A second `Dispose` does nothing. Afterwards the scope tracks nothing:
  no cached instance, initialized object or token registration. A resolve that completes after disposal began with a
  disposable object disposes what it built and throws `ObjectDisposedException` (a non-disposable Transient is simply
  returned, untracked); a pass or startup that observes the disposal stops before the
  next participant. A disposed scope keeps its registrations, `RegisterInstance` values included; drop the reference
  to release them.
- **State.** `resolver.GetLifecycleState()` returns where the scope is: `Created`, `Initializing`, `Initialized`,
  `Starting`, `Ready`, `Faulted`, `Stopping` or `Disposed` (`LifecycleState`). It is safe from any thread, reports
  `Disposed` for a disposed scope instead of throwing, and throws `InvalidOperationException` for a resolver that is not
  the built-in scope. A member may be added to `LifecycleState` in a minor version; code that switches on it keeps a
  default branch.

Trigger the phases on the resolver:

```csharp
resolver.RunInitializables();
await resolver.RunAsyncStartablesAsync(cancellationToken); // after the pass, from the main thread
resolver.InitializeInstance(handBuiltObject); // externally supplied objects, after the pass
await resolver.ShutdownAsync(cancellationToken); // when the owner ends the scope and can await
LifecycleState state = resolver.GetLifecycleState(); // any thread, any state
```

### Shutdown

`await resolver.ShutdownAsync(token)` stops lifecycle work on the scope and its descendants, cancels their participant
tokens, waits for any running startup to settle (the running participant returns), then disposes the scope on the
caller's synchronization context. Resolves keep working while it waits; lifecycle calls are refused. The token bounds
the wait: when it is cancelled the scope is disposed at once and the task completes as cancelled. Disposal failures
fault the task with `Dispose`'s aggregate; on a scope already disposed the task completes once that disposal has
finished. Never await it from inside a `StartAsync` of the scope or a descendant while that startup runs; it is
refused with `InvalidOperationException`. `Dispose` remains the immediate path: it cancels and disposes without
waiting.

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

On `Awake` the scope builds its resolver (or takes the one given to `SetResolver` before `Awake`), walks its scene
once (inactive objects included, `ScopeBoundary` subtrees skipped), binds every MonoBehaviour of that walk that has
`[Inject]` members, runs `RunInitializables` for its services, then initializes the walk's `IInitializable`
components once each; a component a service adds during the pass is in neither list. A scene holds one host: when
it holds another host outside a boundary, `Awake` throws `ScopeOwnershipException` before anything is bound (see
[Boundaries and ownership](#boundaries-and-ownership)). A step that throws throws out of `Awake`, and nothing starts
startables. If you override
`Awake`, call `base.Awake()` first or the scene walk and both passes are skipped.
`[DefaultExecutionOrder(int.MinValue + 100)]` makes this `Awake` run early among scripts Unity orders by execution
order; it is not a guarantee against every script.

`OnDestroy` disposes a self-built resolver and logs each cleanup failure as its own console entry; it does not throw.
`Resolver` is null after `OnDestroy`, whoever owned it, so a subclass reads it before calling `base.OnDestroy()`.
`SetResolver` after `Awake` or after `OnDestroy` throws `InvalidOperationException`.

### Explicit host

For a scene a coordinator loads additively and prepares when it chooses, put `ExplicitScopeHost` in the scene instead
of a `LifetimeScopeBehaviour`:

1. **Nothing happens on its own.** It implements no Unity message but `OnDestroy` and never builds a resolver. A host
   nobody attaches stays `Detached`; the objects in its scene stay unbound and uninitialized.
2. **Attach.** `Attach(resolver, ownership)` hands it a built-in resolver while it is `Detached`, before or after
   `Awake`, active or inactive. It refuses a null resolver, a second attach, a resolver that is disposed, stopping or
   running a pass or startup elsewhere, and a destroyed host. A `Created`, `Initialized`, `Ready` or `Faulted`
   resolver is accepted; a faulted one makes `PrepareAsync` report its fault. It reads no scene.
3. **Ownership.** `ScopeOwnership.Owned`: shutting the host down, or destroying it, shuts the resolver down.
   `ScopeOwnership.Borrowed` (the default value): the host never disposes it. A borrowed scope is prepared by one host
   at a time; prepare a second host after the first is ready.
4. **Prepare.** `PrepareAsync(token)` fixes the batch as the scene's root GameObjects at the call, inactive ones
   included; `PrepareAsync(roots, token)` uses the given roots (null and destroyed entries skipped; they may lie in any
   loaded scene, and each claims a `ScopeBoundary` on itself). It walks the batch once, binds every MonoBehaviour of
   the walk that has `[Inject]` members, runs the scope's initialization pass, initializes the walk's `IInitializable`
   components once, then runs the scope's startup and awaits it. It activates nothing. A call after `Ready` prepares
   nothing; a call while preparing is refused. A batch holding another host outside a boundary, or overlapping given
   roots, is refused with `ScopeOwnershipException` before anything is bound; the host stays `Attached`.
5. **Ready only on success.** The host becomes `Ready` only after every step succeeded while it was neither cancelled
   nor shutting down. A failed step makes it `Faulted` with that exception: `PrepareAsync` and every readiness wait
   fault with it. A cancellation once preparation began (the caller's token, or the scope stopped by its parent's
   shutdown) also makes it `Faulted` and cancels those tasks. A token already cancelled at the call, or a scene that
   cannot be read, changes nothing. `Faulted` lasts until shutdown. A borrowed scope that is already `Ready` is
   supported.
6. **Readiness.** `WaitUntilReadyAsync(token)` may be awaited by any number of callers. It completes on `Ready`, faults
   with the fault, and is cancelled when the host shuts down before `Ready` or the caller's own token fires (that wait
   only). It is refused when `Detached` and from inside the host's own startup.
7. **Shutdown.** `ShutdownAsync(token)` cancels a running prepare, waits for it to settle, shuts an owned resolver down
   with `ShutdownAsync` (or disposes it at once when the core refuses to wait because the caller is inside a startup
   of that scope or a descendant), clears `Resolver` and leaves the host `Detached`. The token bounds the waits; an
   owned resolver is shut down anyway. Cleanup failures fault the task with one `AggregateException`; concurrent calls
   share one shutdown, and a joining call stops waiting at its own token; a call from inside the host's own startup
   is refused, even while a shutdown runs. Afterwards `Attach` may hand the host a
   new scope, and preparing binds and initializes the scene again, so a recycled scene's `Initialize` must be
   repeat-safe.
8. **Destruction.** `OnDestroy` (a scene unload, `Destroy`) starts the same shutdown without waiting and logs each
   failure as its own entry; destroyed from inside its own startup, it disposes an owned resolver at once. Prefer
   awaiting `ShutdownAsync` before unloading. A host whose GameObject was never active receives no `OnDestroy`; shut
   it down explicitly.
9. **Main thread.** Call every member on Unity's main thread; its awaits resume there. A public method called on
   another thread is refused with `InvalidOperationException` before anything changes.
10. **Before preparation.** Objects in the scene receive `Awake` and `OnEnable`, and may receive `Start`, before the
    coordinator can prepare the host; Lattice promises no order. Keep dependency and gameplay work in `Initialize` or
    later, or author the scene's roots inactive and activate them after `Ready`. Disabling the host component is not a
    lifecycle barrier. Gameplay gating stays with the game.

```csharp
// In the coordinator, after the scene has loaded additively:
IObjectResolver region = bootResolver.CreateChildScope(b => b.Register<IRegionService, RegionService>(Lifetime.Scoped));
ExplicitScopeHost host = ExplicitScopeHost.FindInScene(regionScene);
host.Attach(region, ScopeOwnership.Owned);
await host.PrepareAsync(cancellationToken);
// elsewhere: await host.WaitUntilReadyAsync(cancellationToken);

// Before unloading the scene:
await host.ShutdownAsync(cancellationToken);
await SceneManager.UnloadSceneAsync(regionScene);
```

### Boundaries and ownership

Both hosts and the extensions share one walk and one set of rules:

1. **One walk.** Each root of a batch is walked once, inactive objects included, and both lists come from that walk:
   the components to bind and the components to initialize. An object created, or a component added, during the
   scope's pass is in neither; bind and initialize it explicitly. Order within a batch is not a contract.
2. **What is touched.** Only MonoBehaviours. A component is bound when its type has an `[Inject]` field, writable
   property or method, inherited ones included; one without is never passed to `Inject`. It is initialized when it
   implements `IInitializable`. Missing scripts and destroyed components are skipped: a component destroyed with
   `DestroyImmediate` during the pass is not initialized, while one removed with `Object.Destroy` is alive until the
   end of the frame and is still initialized. `IAsyncStartable` components are not started.
3. **Boundaries.** Put a `ScopeBoundary` on the root of a subtree another scope prepares. Every walk skips that
   GameObject and its descendants unless it is a root passed to the call (`InjectGameObject`, `InitializeGameObject`,
   `InstantiateAndInject`, `PrepareAsync(roots, token)`); a scene host also skips one on a scene root. A walk that
   claims a boundary still skips the boundaries beneath it. An inactive boundary object and a disabled marker still
   count. The marker binds, initializes and destroys nothing.
4. **One owner.** A host's batch may hold no other scope host (`LifetimeScopeBehaviour` or `ExplicitScopeHost`, active
   or not, enabled or not) outside a boundary, and given roots may not overlap (one given twice, or one beneath
   another). Every violation is reported in one `ScopeOwnershipException`, raised before anything is bound, naming each
   object as `scene/path` and the fix. The automatic host throws it from `Awake`; the explicit host faults `PrepareAsync`
   with it, changes nothing and stays `Attached`, so it can prepare once the conflict is gone. A `LifetimeScopeBehaviour`
   cannot sit inside a boundary; an `ExplicitScopeHost` inside one belongs to that subtree's preparer.
   `InjectGameObject` and `InitializeGameObject` check boundaries but never hosts.
5. **One host per scene** follows from rule 4. A host removed with `Object.Destroy` still counts until the end of the
   frame: prepare its replacement on a later frame, or remove it with `DestroyImmediate`.
6. **Finding a host.** `ExplicitScopeHost.FindInScene(scene)` returns a loaded scene's one explicit host, inactive ones
   included and boundary subtrees excluded. It throws `InvalidOperationException` when there is none (even with an
   automatic host), `ScopeOwnershipException` when there are several or an automatic host as well, and
   `ArgumentException` for an invalid scene. It attaches and prepares nothing.
7. **Main thread.** Discovery runs on Unity's main thread. `InjectGameObject`, `InitializeGameObject` and `FindInScene`
   throw `InvalidOperationException` on any other thread, before touching anything, a null root included.

```csharp
// A subtree with its own scope: its root carries a ScopeBoundary, so the region's host skips it.
// The session's host sits inside that subtree and is given its root.
IObjectResolver session = region.CreateChildScope(b => b.Register<ISessionService, SessionService>(Lifetime.Scoped));
sessionHost.Attach(session, ScopeOwnership.Owned);
await sessionHost.PrepareAsync(new[] { sessionRoot }, cancellationToken);
```

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

1. Do not read injected fields in `Awake()`. Under `LifetimeScopeBehaviour` they are populated by `Start()`; under
   `ExplicitScopeHost` they are populated once `PrepareAsync` bound them, which may be after `Start()`.
2. Put post-injection setup in `IInitializable.Initialize()`, which runs after Phase 1 completes across the batch,
   after the scope's services are initialized, and, under `LifetimeScopeBehaviour`, before `Start()`. Components are initialized once per scope;
   their order relative to each other is not a contract.
3. Inject dynamically spawned prefabs explicitly, before their `Start()` runs. `InstantiateAndInject` binds only;
   initialize the clone's components afterwards with `InitializeGameObject`:

```csharp
SpawnedThing instance = resolver.InstantiateAndInject(prefab);
resolver.InitializeGameObject(instance.gameObject);
// or, after a manual Instantiate:
resolver.InjectGameObject(spawnedGameObject);
resolver.InitializeGameObject(spawnedGameObject);
```

Inherited members are bound: `[Inject]` fields and properties declared on a base class, private ones included, and
the `[Inject]` method of the most-derived class that declares one (a base class's `[Inject]` method is then not
called). Mark an overridden property `[Inject]` on one level only: marked on both the base and the override, it is
resolved and set twice. `InjectGameObject` and `InitializeGameObject` must be called on Unity's main thread.

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

Preserve `[Inject]` members as well (`link.xml` or `[Preserve]`): a stripped `[Inject]` member makes its component
look member-less, so the hosts and `InjectGameObject` skip it silently, as an unbound member was before.

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
full requested-type chain. A seventh, `ChildSingletonRegistrationException`, is thrown by `CreateChildScope` when a
child registers a Singleton; carries `ServiceType` and `ImplType`.
Lifecycle failures also derive from it: `LifecycleException` is thrown by `RunInitializables` and
`InitializeInstance` when a participant's `Initialize` throws; it carries `ParticipantType` and `Phase`, with the
participant's exception as `InnerException`, and the scope is faulted afterwards.

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
