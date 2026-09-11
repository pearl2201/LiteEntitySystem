---
description: Spawning entities inside client prediction - AddPredictedEntity, matching with the server copy, and the rules that keep it consistent.
---

# Predicted spawning

Normally only the server creates entities, so a client waits a round trip before anything it spawns appears. `AddPredictedEntity` removes that wait: the client creates a local copy immediately, and when the server's authoritative entity arrives it silently replaces the local one.

## When to use this

* Projectiles — the classic case: the bullet leaves the barrel on the shooter's screen at the moment of the click.
* **Any entity representing something the player initiates.** If your game models an *action*, *ability cast*, *dash* or *channelled effect* as an entity, predicted spawning starts it locally the instant the input is applied, with the same matching and correction rules.
* Short-lived entities in general — anything created by predicted code that the player should see without delay.

## When not to

* Entities the server decides about — loot drops, spawned waves, anything not initiated by the local player's input.
* Long-lived world objects: prediction is worth it for what must appear *now*; a structure that takes a second to appear costs nothing in perceived latency.
* Anything created inside a server-to-client RPC handler — that is rejected outright (see below).

## Minimal example

**Fireball.cs**

```csharp
using LiteEntitySystem;

[EntityFlags(EntityFlags.UpdateOnClient)]
public partial class Fireball : PredictableEntityLogic
{
    [SyncVarFlags(SyncFlags.Interpolated)]
    public SyncVar<float> X;

    public SyncVar<float> Speed;

    public Fireball(EntityParams entityParams) : base(entityParams) { }

    protected override void Update()
    {
        // remote clients only render it; the owner and server simulate
        if (IsClient && IsRemoteControlled)
            return;

        X.Value += Speed.Value * EntityManager.DeltaTimeF;
    }

    protected internal override void OnEntityRecreated(PredictableEntityLogic localPredictedEntity)
    {
        // the server's copy took over from the local one — move the view here if needed
    }
}
```

**Caster.cs — spawning from predicted code**

```csharp
protected override void Update()
{
    if (!_castTimer.IsTimeElapsed || !CurrentInputHasCast())
        return;

    _castTimer.Reset();

    // called on server AND on the owning client, including during rollback
    AddPredictedEntity<Fireball>(f => f.Speed.Value = 10f);
}
```

## How it works

### One call, three contexts

`AddPredictedEntity` is written once and behaves correctly in all three situations it runs in:

* **Server** — creates the real entity and assigns it a predicted id derived from the spawner.
* **Owning client, normal simulation** — creates a local entity that exists only on this client, with the same predicted id.
* **Owning client, rollback re-simulation** — does *not* create anything; it looks up the entity already created for that tick and returns it, so re-simulation stays consistent.

The spawner is the entity whose code calls it; the new entity becomes its child and inherits its owner.

### Matching and replacement

The pairing key is (predicted id, spawner, creation tick). When the server's copy arrives, the client finds the local entity with the same key, copies its field values across so change callbacks see continuity, sets `IsRecreated`, and calls `OnEntityRecreated(localPredictedEntity)` on the new entity — the place to migrate anything the local one owned, such as a view object. The local copy is then removed once the server has processed past its creation tick.

If no match is found — the server did not create the entity, because the shot was rejected or the state diverged — the local copy is simply dropped at the next correction. A mispredicted spawn disappears; nothing else has to be undone.

### The reference overload

When the spawner needs to keep a handle on what it spawned, use the overload that fills a `SyncVar<EntitySharedReference>`:

```csharp
private SyncVar<EntitySharedReference> _activeBeam;

// the reference points at the local entity, then at the server's copy after matching
AddPredictedEntity<Beam>(ref _activeBeam, b => b.Length.Value = 5f);
```

### Restrictions

* **Never from an RPC handler.** Spawning inside a server-to-client RPC is rejected and logged: the call would produce entities the re-simulation path cannot reconstruct, and can loop.
* **Only on entities the client controls.** Calling it on a remote-controlled entity is rejected — there is no input history to reproduce the spawn.
* **The type must derive from `PredictableEntityLogic`.** Plain `EntityLogic` has no matching data.
* **Registration must match**, like any entity type, on both sides.

## Behavior details

# [Server](#tab/server)

The entity is created normally and synchronized to everyone; the predicted id travels with it so owners can match. Non-owning clients see an ordinary spawn.

# [Client](#tab/client)

The owner sees the local entity instantly; other clients only ever see the server's copy, arriving after the round trip. Locally created entities live in the client's local id range and are invisible to everyone else.

# [Prediction / rollback](#tab/prediction-rollback)

Re-simulation does not re-create predicted entities — the call resolves to the existing one. Entities created *after* the tick being replayed are skipped during that replay, so a projectile does not advance before it existed.

***

> [!WARNING]
> **Common mistakes**
>
> * Calling `AddPredictedEntity` inside a server-to-client RPC handler — rejected and logged; spawn from `Update` of predicted code instead.
> * Guarding the call with `IsServer` or `InNormalState` — it must run in rollback too, or re-simulation loses track of the entity.
> * Predicting entities the local player did not initiate — without input to reproduce them, every one is a mispredict.
> * Forgetting the remote-client guard in the entity's `Update` — with `UpdateOnClient`, clients would simulate an entity they do not own and have no inputs for.

## Related pages

- [adaptive-timing.md](adaptive-timing.md)
- [prediction-and-rollback.md](prediction-and-rollback.md)
