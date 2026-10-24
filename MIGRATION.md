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
