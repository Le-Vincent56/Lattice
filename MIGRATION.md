# Migration guide

One section per major version, newest first. Each entry says what changed and what a consumer does about it.

## 2.0.0

### Parent-owned Scoped services are built by the parent

A Scoped service registered in a parent scope is now constructed with the parent as its resolver. A child's
registration of one of its dependencies no longer reaches it, even when the child is the first to request it, and
the `IDisposable` Transients created while building it are disposed with the parent, not with that child.

If the dependency is registered in both scopes, the parent's registration is used. If it is registered only in
the child, the parent cannot see it and the resolve throws `RegistrationNotFoundException` naming the missing type.

What to do: if a child relied on overriding or supplying a dependency of a parent-owned Scoped service, register
the service itself in the child with `Lifetime.Scoped`, or register the dependency in the parent. A service
registered in the child gets its own instance built from the child's registrations.

### `CreateChildScope` validates against the creating scope

The child's registrations are now walked against the creating scope and every ancestor, so a cycle that crosses
into the immediate parent throws `CyclicDependencyException` at `CreateChildScope` instead of surfacing at the
first resolve.

What to do: the cycle was already a defect; break it. Nothing changes for graphs without one.

### Singletons are root registrations

`CreateChildScope` throws `ChildSingletonRegistrationException` when the child registers a service with
`Lifetime.Singleton`, through `Register`, `RegisterFactory`, `RegisterOpenGeneric`, or `PreserveClosedGenerics`
against a parent's open Singleton. Previously such a registration was cached by the root under the child's entry:
it outlived the child and was rebuilt on every creation of that child.

This includes any `IInstaller` applied inside a child's configure delegate. An installer written for the root
cannot be reused in a child unless every registration in it is Scoped, Transient or an instance.

What to do: change the registration to `Lifetime.Scoped`. The child observes the same single instance, now disposed
with the child. `RegisterInstance` in a child is still allowed.

### Aliases share one instance

Every `As<T>()`, `AsSelf()` and `AsImplementedInterfaces()` alias of a Singleton or Scoped registration now
resolves the same instance. Previously each alias built and cached its own. Decorators still apply per service
type: resolving a decorated alias returns the decorator around the shared instance, and resolving any other alias
returns the shared instance undecorated. The inner instance of a decorated service is now disposed with its scope and
initialized by `RunInitializables`, and `RunAsyncStartablesAsync` starts an aliased instance once per call.

What to do: if code relied on two aliases being two objects, register the implementation twice with two `Register`
calls instead of aliasing. If a decorator forwards `Initialize` or `Dispose` to the instance it wraps, stop
forwarding: the container now makes both calls on the inner instance itself, so forwarding runs each one twice.

### Lifecycle participants are built by the pass

`RunInitializables` now materializes every Singleton or Scoped registration of its scope whose implementation or
decorator type implements `IInitializable`, then initializes them in creation order. A participant nobody resolved
is no longer skipped, and a participant whose dependency is unregistered now fails at the pass.

What to do: nothing for services you resolved before the pass. If a registered `IInitializable` must not be
constructed at startup, register it as Transient or drop the interface. A factory whose service type is not
`IInitializable` participates only if its result was already created; to make it participate, register the concrete
type and alias the interface: `RegisterFactory<Loader>(..., Lifetime.Singleton).As<ILoader>()`. Closed generics
participate once preserved with `PreserveClosedGenerics`. `RegisterInstance` values are caller-owned; call
`resolver.InitializeInstance(instance)` after the pass if one needs `Initialize`.

### Initialize failures fault the scope

A thrown `Initialize` arrives as `LifecycleException` (the original exception is `InnerException`) and the scope is
faulted: further `RunInitializables` and `InitializeInstance` calls on it and its children throw. Dispose the scope
and build a new one; do not retry the pass. Code that caught the participant's own exception type catches
`LifecycleException` or `DependencyResolutionException` instead.

### Child scopes initialize after their parent

`RunInitializables` on a child throws `InvalidOperationException` until the parent's pass has completed. A
coordinator that hands a child resolver to a `LifetimeScopeBehaviour` runs `RunInitializables` on the root first.

### Scene components initialize after services, once

`LifetimeScopeBehaviour` runs the service pass before component `Initialize`, and `InitializeGameObject` initializes
a component once per scope and throws before the scope's own pass. A component that relied on running before
services, or on a second `InitializeGameObject` call re-running `Initialize`, moves that work elsewhere.
`InstantiateAndInject` still binds only; initialize a spawned object's components with `InitializeGameObject` after
the scope's pass.

### Async startup runs after the initialization pass, once

Call `RunInitializables` before `RunAsyncStartablesAsync`, on the parent before the child, and start the parent
before the child. The pass now builds startable registrations, so a startable nobody resolved is constructed at the
pass; a `LifetimeScopeBehaviour` runs the pass in `Awake`, so its startable constructors now run there, whether or not
anything starts them. Keep `Awake`-time work out of startable constructors. Startup starts what the pass built, in
creation order, once per scope; a second call does nothing and a concurrent call throws. A startable registered under
an interface that does not extend `IAsyncStartable` now starts. Transient startables no longer start: register them
as Singleton or Scoped. `RegisterInstance` values are no longer started: call `StartAsync` on them yourself, or
register the type. Startup throws when a cached startable that is also `IInitializable` was created after the pass:
run `RunInitializables` again before startup. After startup has begun, `RunInitializables` initializes nothing; it
throws when an `IInitializable` created since is pending, so call `resolver.InitializeInstance(obj)` for such objects.
A scene host handed a started scope through `SetResolver` keeps working.

### Startup failures and cancellation fault the scope

A thrown `StartAsync` arrives as `LifecycleException` (`Phase == LifecyclePhase.Start`, original exception inside) and
faults the scope. Cancelling the token after startup began, or disposing the scope during startup, stops further
participants, faults the scope and cancels the returned task. Dispose and rebuild; do not retry. Participants should
observe the token they receive and return promptly; Lattice waits for a running participant and never abandons it.
Participants receive a token the scope owns, not the caller's token itself; see "The participant token lasts until
disposal" below.

### Startup continues on the caller's context

Call `RunAsyncStartablesAsync` from the Unity main thread and await it; do not block on the task. Each participant
then runs on the main thread even when an earlier participant's task completed on a worker thread. Code that relied
on later participants running on a thread-pool thread moves that work into `Task.Run` inside its own `StartAsync`.
Code inside a participant after its own `ConfigureAwait(false)` still runs wherever that await resumed.

### Dispose reports cleanup failures

`Dispose` no longer swallows exceptions. It disposes everything and then throws one `AggregateException` listing each
failure. Code that disposed scopes in `finally` blocks or `using` statements should expect it; in Unity,
`LifetimeScopeBehaviour` logs each failure. Make each `Dispose` method you own safe to call once and quiet on success.
An exception thrown by `Dispose` while a `using` block unwinds replaces the exception it was unwinding. In a
`LifetimeScopeBehaviour` subclass, call `base.OnDestroy()` last, after your own cleanup, so that cleanup still sees
`Resolver`. A `Dispose` method must not resolve from the scope being disposed: that resolve throws
`ObjectDisposedException`, which now joins the aggregate.

### Shut scopes down with ShutdownAsync

Where an owner can await (a scene transition, an async exit path), replace `resolver.Dispose()` with
`await resolver.ShutdownAsync(token)`: it cancels startup work, waits for the running participant to return, then
disposes on your context. Pass a token to bound the wait. `Dispose` remains the immediate path. Do not await it from
inside a `StartAsync` of the same scope.

### The participant token lasts until disposal

Work a startup participant runs with the token it received is now cancelled when the scope is disposed or shut down.
A participant that kept the token for background work no longer needs a token of its own for that. Work that must
outlive the scope needs its own token.

### Every decorator in a chain is owned

Intermediate decorators are now disposed, initialized and started, and a Transient's inner instance and decorators are
disposed with the scope. Remove any forwarding of `Initialize`, `StartAsync` or `Dispose` from a decorator to the
object it wraps.

### Drive additively loaded scenes with ExplicitScopeHost

A scene host that must receive a scope its coordinator built no longer needs `SetResolver` to win a race with `Awake`.
Put `ExplicitScopeHost` in the scene instead of a `LifetimeScopeBehaviour` subclass. After the scene loads, create the
child from the parent you own, then `host.Attach(child, ScopeOwnership.Owned)`, `await host.PrepareAsync(token)`; other
systems `await host.WaitUntilReadyAsync(token)`. Before unloading, `await host.ShutdownAsync(token)`. To hand a loaded
scene to a new scope, shut the host down and attach the new scope; components are bound and initialized again, so
their `Initialize` must be repeat-safe. Objects in the scene still receive `Awake` and `OnEnable`, and may receive
`Start`, before preparation: keep dependency work in `Initialize`, or author the scene's roots inactive and activate
them after the host is ready.

### LifetimeScopeBehaviour after OnDestroy

`Resolver` is null after `OnDestroy`; keep your own reference if you need the resolver later. Cleanup failures of a
self-built resolver are logged one per entry and no longer thrown, so code in a subclass's `OnDestroy` after
`base.OnDestroy()` always runs. A subclass that reads `Resolver` in its own `OnDestroy` reads it before calling
`base.OnDestroy()`.

### One scope host per scene, and ScopeBoundary for sub-scopes

A scene may hold one scope host (`LifetimeScopeBehaviour` or `ExplicitScopeHost`). A second host outside a
`ScopeBoundary` makes the host refuse with `ScopeOwnershipException` before anything is bound: the automatic host throws
from `Awake`, the explicit host faults `PrepareAsync`. To give a subtree its own scope, put a `ScopeBoundary` on its root
and prepare that root with the child scope (an `ExplicitScopeHost` given the root, or `InjectGameObject` then
`InitializeGameObject` with the child resolver); the scene's host then skips it. A `LifetimeScopeBehaviour` cannot sit
inside a boundary: it is always its scene's host. A host removed with `Object.Destroy` still counts until the end of
the frame; prepare its replacement on a later frame, or use `DestroyImmediate`. Find the explicit host of a loaded
scene with `ExplicitScopeHost.FindInScene(scene)` instead of a lookup of your own.

### One walk per batch

A host initializes only the components its walk found before the service pass. A component that a service's
`Initialize` adds beneath a scene root is no longer initialized by the host; call `InjectGameObject` and
`InitializeGameObject` on it. A MonoBehaviour with no `[Inject]` members is no longer passed to `Inject`; a custom
`IObjectResolver` that relied on seeing every component must walk the hierarchy itself. `InjectGameObject` and
`InitializeGameObject` must be called on Unity's main thread, even with a null root, which used to return silently on
any thread.
