---
description: Declaring and using SyncVar fields - the .Value contract, supported types, registering custom ones, and reading interpolated values.
---

# SyncVar&lt;T&gt;

`SyncVar<T>` is the basic unit of synchronized state: a struct wrapper around an unmanaged value that reports every change to the manager, so the server can ship exactly the fields that moved.

## When to use this

* Any entity state clients must agree on: position, health, ammo, a phase enum, a reference to another entity.
* Values read every frame by views — with `SyncFlags.Interpolated`, they gain a smoothed companion value.

## When not to

* Values only one side needs (bot timers, cached view objects, local accumulators) — plain C# fields; SyncVars cost bandwidth and are useless to a server-only or client-only concern.
* Strings, collections and other managed data — those are [`SyncableField`](syncablefield-basics.md) types like `SyncString` and `SyncList<T>`.
* One-shot events — an [RPC](rpc-basics.md); state answers "what is it now", not "what happened".

## Minimal example

**Reactor.cs**

```csharp
using LiteEntitySystem;

public enum ReactorMode : byte { Idle, Charging, Venting }

[EntityFlags(EntityFlags.Updateable)]
public partial class Reactor : EntityLogic
{
    [SyncVarFlags(SyncFlags.Interpolated)]
    public SyncVar<float> Heat;

    public SyncVar<ReactorMode> Mode;
    public SyncVar<EntitySharedReference> Operator;

    public Reactor(EntityParams entityParams) : base(entityParams) { }

    protected override void Update()
    {
        if (Mode == ReactorMode.Charging)       // implicit conversion, no .Value needed
            Heat.Value += EntityManager.DeltaTimeF;

        if (Heat > 100f)
            Mode.Value = ReactorMode.Venting;   // assignment always goes through .Value
    }
}
```

## How it works

### The `.Value` contract

Reading works either way — `.Value` or the implicit conversion to `T` (`if (Heat > 100f)`). Writing must always go through `.Value`. Assigning the field itself (`Heat = new SyncVar<float>()`) replaces the wrapper, losing its link to the entity, and synchronization for that field silently dies. The shipped Roslyn analyzer turns this into a compile error — see [installation](../getting-started/installation.md).

Each write compares the bytes and only marks the field dirty when the value actually differs, so assigning the same value every tick costs nothing on the wire.

### Why the class is `partial`

The synchronization system reaches each `SyncVar<T>` field from code that only knows the entity's *class id*, not its concrete type. A Roslyn source generator emits a typed accessor for every field you declare and writes them into a partial declaration of your class — which is why the modifier is required, and why `SyncVar<T>` fields cannot be `readonly`. Do not confuse the two rules: a `SyncableField` **member** must be `readonly`, while the `SyncVar<T>` fields *inside* it must not. Rules, generated shape, and the hand-written escape hatch are on the [source generation page](../integration/source-generation.md).

### Which types are allowed

Any `unmanaged` type: primitives, enums, and structs of those. `EntitySharedReference` is supported out of the box — that is how entity references are stored — as is `FloatAngle` for values that wrap at 360°.

Engine types like `Vector2` are unmanaged but unknown to the library until registered, once per process on both sides, before creating managers:

```csharp
EntityManager.RegisterFieldType<Vector2>(Vector2.Lerp);   // with interpolation
EntityManager.RegisterFieldType<MyPackedStruct>();        // without
```

Pass a lerp function for anything you intend to mark `Interpolated`; omit it for types that only ever snap.

### Reading interpolated values

A field marked `SyncFlags.Interpolated` keeps a second, smoothed value in `InterpolatedValue`. The rule of thumb: logic reads `.Value`, views read `.InterpolatedValue`. Details of what it interpolates between — and why the answer differs for owned and remote entities — are in the [interpolation page](../netcode/interpolation.md).

### Behavior flags and change callbacks

`[SyncVarFlags(...)]` on the field (or on the whole class as a default) controls interpolation, lag compensation, per-owner visibility, rollback participation and sync groups; each flag has its own row in the [SyncFlags reference](sync-flags.md). To react to a value arriving from the server, bind a callback in `RegisterRPC` — see [change notifications](bind-on-change.md). When you need to change a value without triggering those callbacks locally, `SetValueWithoutOnSyncNotification` does exactly that.

## Behavior details

# [Server](#tab/server)

A write marks the field dirty for the tick; the next [state update](how-state-syncs.md) carries it to the clients that are allowed to see it. Values written before the entity is constructed (in an init method) are part of its very first packet.

# [Client](#tab/client)

Incoming states overwrite `.Value` directly — no callbacks unless bound. A write made by client code is a local prediction: it holds until the next server state for that field replaces it.

# [Prediction / rollback](#tab/prediction-rollback)

On locally controlled entities, predicted fields are reset to the last server value at every rollback and re-derived by re-simulation. On entities the client does not own, a field only participates if marked `SyncFlags.AlwaysRollback`; `NeverRollBack` opts a field out entirely. See [controlling rollback](../netcode/rollback-per-field.md).

***

> [!WARNING]
> **Common mistakes**
>
> * Assigning the wrapper instead of `.Value` — sync for that field dies silently; install the analyzer so it cannot compile.
> * `SyncVar<Vector2>` (or any engine type) without `RegisterFieldType` — fails at startup; register on both sides before creating managers.
> * Reading `.Value` in view code — visuals step at the send rate; views read `.InterpolatedValue`.
> * Storing a direct entity reference in a SyncVar-like field — only `EntitySharedReference` survives serialization and id reuse.

## Related pages

- [sync-flags.md](sync-flags.md)
- [bind-on-change.md](bind-on-change.md)
