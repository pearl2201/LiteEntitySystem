---
description: SingletonEntityLogic for one-per-world synced services, and ILocalSingleton for non-networked ones - spawning, lookups, update hooks.
---

# Singletons

Two kinds of "exactly one" live side by side: `SingletonEntityLogic` is a synchronized entity that exists once per world (match rules, the physics manager), and `ILocalSingleton` is a plain non-networked service attached to a manager (audio, pools, a message bus).

## When to use this

* World-level synced state every player must agree on: match phase, round timer, team scores.
* Wrapping an engine service the simulation depends on — the example project's `UnityPhysicsManager` owns the per-scene physics world this way.
* Non-networked per-manager services — as `ILocalSingleton`, not as an entity.

## When not to

* Anything per-player — that is the controller ([owner-private](controllers-in-depth.md)) or the pawn.
* Things that can multiply — plain entities; the singleton contract is exactly one.
* Static config that never changes at runtime — ship it with the build; entities are for state.

## Minimal example

**MatchState.cs**

```csharp
using LiteEntitySystem;

[EntityFlags(EntityFlags.Updateable)]
public partial class MatchState : SingletonEntityLogic
{
    public SyncVar<float> RoundTime;
    public SyncVar<byte> ScoreRed;
    public SyncVar<byte> ScoreBlue;

    public MatchState(EntityParams entityParams) : base(entityParams) { }

    protected override void Update()
    {
        RoundTime.Value -= EntityManager.DeltaTimeF;
    }
}
```

**Spawning and looking up**

```csharp
// server, at world start — before entities that depend on it:
_entityManager.AddSingleton<MatchState>();

// anywhere, any side:
if (_entityManager.TryGetSingleton(out MatchState match))
    ShowTimer(match.RoundTime);
```

## How it works

### A normal entity with an invariant

A singleton registers, spawns (`AddSingleton<T>()`) and syncs like any entity, and takes the same `EntityFlags`. What changes: it is always server-owned, and the manager indexes it by type for `GetSingleton<T>()` / `TryGetSingleton` / `HasSingleton` lookups. Lookups work through base types too — `GetSingleton<MatchState>()` finds a registered `ServerMatchState : MatchState`, so the [server/client variant pattern](../getting-started/registering-entity-types.md) applies to singletons as well.

### Creation order matters

Spawn singletons before the entities that use them: `OnConstructed` of a later entity can then rely on `GetSingleton` (the example's `BasePlayer` grabs `UnityPhysicsManager` there). Client-side construction replays server creation order, so "physics first, players after" holds on every client automatically.

### Local singletons

`AddLocalSingleton(instance)` registers any `ILocalSingleton` on a manager; `GetLocalSingleton<T>` / `TryGetLocalSingleton` retrieve it, base types and interfaces included. The shipped [`LocalMessageBus`](../integration/local-message-bus.md) is one of these. Implement `ILocalSingletonWithUpdate` to receive `Update(dt)` each logic tick, `VisualUpdate(dt)` each render frame and `LateUpdate(dt)` after entity updates. On `Reset()` the manager calls `Destroy()` on every local singleton. Nothing here touches the network — it is dependency wiring for code that lives next to the world.

## Behavior details

# [Server](#tab/server)

`AddSingleton` spawns the instance server-owned; it updates and delta-syncs like every entity. Nothing enforces uniqueness at spawn time — a second `AddSingleton<MatchState>()` would create a second entity and steal the lookup slot; keeping the invariant is your code's job.

# [Client](#tab/client)

The singleton arrives with the baseline (or its spawn packet) and is found via the same lookups afterwards. Before the baseline there is nothing to find — `GetSingleton` returns null while the manager is dormant.

# [Prediction / rollback](#tab/prediction-rollback)

Server-owned means not predicted: clients never simulate `MatchState.Update`. Its fields follow the standard rules — a field a client should predict against (rare for singletons) would need `SyncFlags.AlwaysRollback` like any other server-owned entity's field.

***

> [!WARNING]
> **Common mistakes**
>
> * Calling `GetSingleton` on the client before the baseline arrived — null; use `TryGetSingleton` at call sites that can run early, or defer to `OnConstructed` of dependent entities.
> * Spawning a singleton after the entities that read it in `OnConstructed` — their construction already ran without it; create singletons first at world start.
> * A second `AddSingleton` of the same type — the lookup silently switches to the newest instance while the old entity lives on; the one-instance invariant is not enforced for you.
> * Modeling a client-only service as a `SingletonEntityLogic` — it costs registration, sync and server presence for nothing; `ILocalSingleton` is the tool for non-networked services.

## Related pages

- [update-flags.md](update-flags.md)
- [parent-child.md](parent-child.md)
