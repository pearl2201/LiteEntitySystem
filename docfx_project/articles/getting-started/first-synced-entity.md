---
description: A minimal EntityLogic subclass - SyncVars, an RPC, a change callback, spawning on the server and smooth rendering on the client.
---

# Your first synced entity

This page builds the smallest complete entity: a class with synchronized fields that updates on the server, exists on every client, and renders smoothly through interpolation.

## When to use this

* `EntityLogic` is the base for world objects that are not driven by player input: pickups, doors, zones, match state.
* Anything whose state must be the same for all players belongs in an entity with `SyncVar` fields.

## When not to

* Player-controlled characters — those are pawns possessed by controllers, covered in [adding-a-player.md](adding-a-player.md).
* World services that exist exactly once (physics manager, match rules) — `SingletonEntityLogic` fits better.
* Purely visual objects (particles, decals, UI) — not entities; they stay entirely in engine code.

## Minimal example

A crystal that bobs up and down on the server and is worth a few points:

**ScoreCrystal.cs**

```csharp
using System;
using LiteEntitySystem;

[EntityFlags(EntityFlags.Updateable)]
public partial class ScoreCrystal : EntityLogic
{
    [SyncVarFlags(SyncFlags.Interpolated)]
    public SyncVar<float> Height;

    public SyncVar<byte> Score;

    private static RemoteCall _sparkleRemoteCall;

    public ScoreCrystal(EntityParams entityParams) : base(entityParams) { }

    protected override void RegisterRPC(ref RPCRegistrator r)
    {
        base.RegisterRPC(ref r);
        r.CreateRPCAction(this, OnSparkle, ref _sparkleRemoteCall, ExecuteFlags.SendToAll);
        r.BindOnChange(this, ref Score, OnScoreChanged);
    }

    // server-side: makes every client play the effect once
    public void Sparkle() => ExecuteRPC(_sparkleRemoteCall);

    private void OnSparkle()
    {
        // play a one-shot effect on the view
    }

    private void OnScoreChanged(byte prevScore)
    {
        // Score just changed by sync from the server; update the view's label
    }

    protected override void Update()
    {
        base.Update();
        Height.Value = MathF.Sin(EntityManager.Tick * EntityManager.DeltaTimeF) * 0.25f;
    }

    protected override void OnConstructed()
    {
        if (IsClient)
        {
            // create the view object of your engine here
        }
    }

    protected override void OnDestroy()
    {
        // destroy the view object here
    }
}
```

## How it works

### The class flag

`[EntityFlags(EntityFlags.Updateable)]` is what makes `Update()` run — without it the entity is constructed and synchronized, but its `Update()` is never called. For a server-owned entity like this one, `Update()` runs on the server every logic tick.

### The synchronized fields

Two `SyncVar` fields with different needs. `Score` is plain data — it syncs whenever it changes. `Height` changes every tick and is rendered, so it carries `SyncFlags.Interpolated`: the client keeps an interpolated copy that moves smoothly between the last two received states instead of stepping at the send rate.

### An RPC for one-shot events

`SyncVar` is state: whoever connects later still sees the current value. For events that happen once — an effect, a sound — use an RPC. The handle is a `static RemoteCall` field, registered in the `RegisterRPC` override with `CreateRPCAction` and executed on the server with `ExecuteRPC`; `ExecuteFlags.SendToAll` delivers it to every client, in order with the state stream. The handle is static because RPC registration happens once per class, not per instance — and the first line of any `RegisterRPC` override must be `base.RegisterRPC(ref r)`.

### Reacting to server changes

`r.BindOnChange(this, ref Score, OnScoreChanged)` binds a callback that fires on the client when a state update changes `Score` — the right place to update a label or play a reaction, instead of comparing values manually every frame. The callback receives the *previous* value; the field itself already holds the new one. By default it fires only on sync from the server; other timings (server-side, prediction) are opt-in via `BindOnChangeFlags`.

### Registering and spawning

Like every entity class, the crystal gets an enum member and a registration on both sides ([registering-entity-types.md](registering-entity-types.md)), and the server spawns it:

**On the server**

```csharp
var crystal = _entityManager.AddEntity<ScoreCrystal>(e => e.Score.Value = 10);
```

The init method runs before `OnConstructed`, so initial values like `Score` are part of the entity's very first packet — clients never see a half-initialized crystal.

### The client side

The client constructs the crystal automatically when it arrives in a state update. `OnConstructed` is where the entity creates its view (an engine object), and `OnDestroy` removes it. In your per-frame view code, read `Height.InterpolatedValue` — not `Height.Value` — to place the view: this is the smoothed value, and it is how the example project moves player views too.

## Behavior details

# [Server](#tab/server)
`Update()` runs every logic tick. Assigning `Height.Value` compares bytes and marks the field changed only when the value actually differs; changed fields are collected into the next delta-compressed state update at the send rate.
# [Client](#tab/client)
The crystal appears with the baseline or with the spawn packet, `OnConstructed` fires after its initial values are applied. Incoming states update `Value` in steps; `InterpolatedValue` blends between the two most recent states, so the view trails the server slightly but moves smoothly.
# [Prediction / rollback](#tab/prediction-rollback)
This entity is server-owned and nothing predicts it, so it does not participate in rollback at all. If a client-side interaction should predict a change to a field of such an entity (as in the door example on the [introduction page](../index.md)), that field needs `SyncFlags.AlwaysRollback`.
***

> [!WARNING]
> **Common mistakes**
>
> * No `[EntityFlags(EntityFlags.Updateable)]` on the class — `Update()` is silently never called; the entity just sits there.
> * Rendering from `.Value` — the view stutters at the send rate. Rendering reads `.InterpolatedValue`, and only fields marked `SyncFlags.Interpolated` have it maintained.
> * Using an engine vector type (`SyncVar<Vector2>`) without registering it first — call `EntityManager.RegisterFieldType<Vector2>(Vector2.Lerp)` on both sides before creating managers; built-in support covers primitives and `FloatAngle`, not engine types.
> * Overriding `RegisterRPC` without calling `base.RegisterRPC(ref r)` — base-class RPCs and bindings are lost; the library logs an error naming the offending class.

## Related pages

- [adding-a-player.md](adding-a-player.md)

- [registering-entity-types.md](registering-entity-types.md)
