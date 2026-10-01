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
