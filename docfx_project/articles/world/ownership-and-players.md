---
description: Player ids, NetPlayer, the three ownership checks, how entities acquire and inherit owners, and everything ownership controls.
---

# Ownership and players

Every entity carries an `OwnerId` — the byte id of the player it belongs to, or `0` for the server — and that single byte decides who predicts the entity, who receives which fields, and whose perspective lag compensation uses.

## Players

On the server, each connection accepted through `AddPlayer` becomes a `NetPlayer` with an id from 1 to 254; id `0` (`EntityManager.ServerPlayerId`) is reserved for the server itself. The client learns its own id with the baseline — `PlayerId` on the manager — and exposes its `NetPlayer` as `LocalPlayer`. Server code can look up any player by owner id via `GetPlayer(entity.OwnerId)`.

## The three checks

| Check | True when | Typical use |
|---|---|---|
| `IsServerControlled` | `OwnerId == 0` | World objects, singletons — nobody's, everybody sees them the same |
| `IsLocalControlled` | `OwnerId` equals this manager's player id | Prediction-side logic, owner-only feedback |
| `IsRemoteControlled` | Anything else | Entities this side only observes, interpolated |

One asymmetry worth internalizing: the server's own player id is `0`, so on the server every server-owned entity reports `IsLocalControlled == true`. The checks describe "mine / not mine" relative to whichever manager is asking, not "server / client".

## Minimal example

A bomb inherits its planter's ownership, so the planter's client predicts it:

**Bomb.cs**

```csharp
using LiteEntitySystem;

[EntityFlags(EntityFlags.Updateable)]
public partial class Bomb : EntityLogic
{
    public SyncVar<float> Timer;

    public Bomb(EntityParams entityParams) : base(entityParams) { }

    protected override void Update()
    {
        Timer.Value -= EntityManager.DeltaTimeF;
        if (IsServer && Timer.Value <= 0f)
            Destroy();
    }

    protected override void OnConstructed()
    {
        if (IsClient)
        {
            // IsLocalControlled → this client planted it: friendly outline, own ticking sound
            // IsRemoteControlled → someone else's bomb: hostile view
        }
    }
}
```

**On the server**

```csharp
// server-owned: OwnerId == 0, nobody predicts it
var crystal = manager.AddEntity<ScoreCrystal>();

// owned by the planter: passes the parent's owner down
var bomb = manager.AddEntity<Bomb>(planterPawn, b => b.Timer.Value = 3f);
```

## How it works

### How entities acquire an owner

Game code never assigns `OwnerId` directly — there is no public setter. Ownership always flows in from structure:

* A plain `AddEntity<T>()` produces a server-owned entity.
* `AddController<T>(player, …)` creates a controller owned by that player, and possession passes the owner on to the pawn.
* `AddEntity<T>(parent, …)` and `AddPredictedEntity<T>` inherit the parent's owner.
* `SetParent` re-propagates the new parent's owner down the whole child subtree.

The consequence: ownership is a property of *where an entity hangs in the possession and parent structure*, and it changes when that structure changes — a pawn released by its controller reverts to server ownership, and every child follows.

### What ownership drives

Prediction: a client simulates only entities it owns; everything else is interpolated. RPC routing: `ExecuteFlags.SendToOwner` / `SendToOther` split on this byte. Field visibility: `SyncFlags.OnlyForOwner` / `OnlyForOtherPlayers` filter per-field, and `EntityFlags.OnlyForOwner` hides whole entities (this is how controllers stay private). Lag compensation rewinds *other* players' entities relative to an owner — the owner is never rewound for their own shot. And on the client, owned `Updateable` entities are the ones that run `Update()` locally.

### Ownership changes at runtime

Possession and reparenting rewrite `OwnerId` on the fly, and the client reacts automatically: an entity that becomes locally controlled starts predicting, one that stops being owned drops back to interpolation. React to these transitions in the hooks that cause them — `OnControlledEntityChanged` on controllers, `OnParentChanged` on entities — rather than polling the checks.

## Behavior details

# [Server](#tab/server)

The server updates every entity regardless of owner; `OwnerId` matters for packet composition — which player receives which fields and RPCs — and as the key for `GetPlayer` when kicking, inspecting input state, or enabling lag compensation for a shooter.

# [Client](#tab/client)

`IsLocalControlled` partitions the world: owned entities tick locally as predictions, remote ones replay server states through interpolation. When ownership flips mid-game, the entity migrates between the two regimes without any game-code involvement.

# [Prediction / rollback](#tab/prediction-rollback)

Rollback re-simulates only locally controlled entities. Fields of remote entities stay untouched unless explicitly marked `SyncFlags.AlwaysRollback` — the mechanism from the [introduction's door example](../index.md), covered fully in the prediction section.

***

> [!WARNING]
> **Common mistakes**
>
> * Caching `IsLocalControlled` at construction — possession and reparenting change ownership at runtime, and the cached flag silently goes stale; check it where you use it, or react in `OnControlledEntityChanged`/`OnParentChanged`.
> * Using `IsClient` where you mean `IsLocalControlled` — an `IsClient` gate runs for *every* client, so "my" feedback (sounds, UI) plays for spectators of other players too.
> * Treating the server's `IsLocalControlled == true` as a client indicator — on the server it merely means server-owned; combine with `IsServer`/`IsClient` when the side matters.

## Related pages

- [controllers-in-depth.md](controllers-in-depth.md)
- [entity-hierarchy.md](entity-hierarchy.md)
