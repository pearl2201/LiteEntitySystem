---
description: The entity lifecycle from constructor to removal - what each hook is for, destroy semantics per side, and how ids get reused safely.
---

# Entity lifecycle

An entity passes through five states — New, Constructed, LateConstructed, Destroyed, Removed — and exposes a hook at each transition; putting code in the right hook is most of what "lifecycle" means in practice.

```
     constructor            OnConstructed           OnLateConstructed
New ──────────────► Constructed ──────────► LateConstructed ──► (alive, Update runs)
                                                       │  Destroy()
                                                       ▼
                                            Destroyed ──► Removed
                                            OnDestroy      id freed, Version++
```

The current state is `CreationState`; `IsConstructed`, `IsDestroyed` and `IsRemoved` are shortcuts.

## Which hook for what

| Hook | What is already valid | Put here |
|---|---|---|
| constructor | Identity only: `Id`, `ClassId`, `Version`, `EntityManager`. Synced values are **not** applied yet | Field defaults (`_speed.Value = 3f`), allocation of plain members |
| `OnConstructed` | All synced values applied; entities created *earlier* exist | View creation on the client, singleton lookups, self-setup |
| `OnLateConstructed` | Every entity constructed in this batch exists | Resolving references to entities spawned the same tick |
| `OnDestroy` | Entity still readable, world still consistent | Releasing views, unsubscribing, dropping pooled resources |

## Minimal example

**Turret.cs**

```csharp
using LiteEntitySystem;

public partial class Turret : EntityLogic
{
    public SyncVar<EntitySharedReference> Target;

    private EntityLogic _resolvedTarget;

    public Turret(EntityParams entityParams) : base(entityParams)
    {
        // identity is set; Target is NOT applied yet — don't read it here
    }

    protected override void OnConstructed()
    {
        // synced values are applied; on the client, create the view here
    }

    protected override void OnLateConstructed()
    {
        // safe even if the target was spawned in the same tick as this turret
        _resolvedTarget = EntityManager.GetEntityById<EntityLogic>(Target);
    }

    protected override void OnDestroy()
    {
        // destroy the view, drop references
    }
}
```

## How it works

### Construction order

On the server, `AddEntity` runs the constructor, then your init method, then `OnConstructed` — which is why init-method values are visible there. On the client, the constructor runs when the entity first arrives, the synced data is applied, and only then `OnConstructed` fires. In both cases `OnLateConstructed` is deferred to the end of the current batch: when it runs, every entity constructed alongside yours already exists, which makes it the safe place for cross-entity wiring.

Client-side construction order follows server-side creation order. A reference to an entity the server created *before* yours is therefore already resolvable in `OnConstructed`; `OnLateConstructed` removes even that ordering concern.

### Destroy semantics

`Destroy()` is a server-side decision. On the server it fires `OnDestroy` immediately, notifies clients as an ordered event, and — for an `EntityLogic` — cascades: children get `OnBeforeParentDestroy` first, then are destroyed too. On the client, `Destroy()` on a synced entity is a no-op; the entity dies when the server says so, and `OnDestroy` is where the client cleans up its view. The only entities a client may destroy itself are its local (predicted) ones.

### Id reuse and Version

A destroyed entity's id is not freed immediately — the server waits until every client has confirmed a state past the destruction, then recycles the id. Each reuse increments the entity's `Version`, and `EntitySharedReference` stores id + version together: a stale reference to the dead entity resolves to `null` via `GetEntityById` instead of silently pointing at the newcomer. This is why entity references in game state should be `EntitySharedReference`, not direct object references.

## Behavior details

# [Server](#tab/server)

Full sequence per spawn: constructor → init method → `OnConstructed` → (end of tick) `OnLateConstructed`. Destruction: `OnDestroy` at the `Destroy()` call, removal and id recycling deferred until all clients are past it. An entity destroyed before its `OnLateConstructed` still gets it, right before `OnDestroy`.

# [Client](#tab/client)

Same hook order, driven by the network: constructor and data on arrival, `OnConstructed` after values apply, `OnLateConstructed` at the end of processing the batch. Destruction arrives in order with state — by the time `OnDestroy` runs, the rest of that server state is already applied.

# [Prediction / rollback](#tab/prediction-rollback)

Rollback re-runs `Update()` only — lifecycle hooks never re-fire during re-simulation. Predicted entities created via `AddPredictedEntity` do run their constructor and `OnConstructed` immediately on the client; what happens when the server's copy arrives is covered by the predicted-spawning page.

***

> [!WARNING]
> **Common mistakes**
>
> * Reading SyncVars or looking up other entities in the constructor — synced values are not applied and the world may not contain them yet; that code belongs in `OnConstructed` or later.
> * Creating the client view in the constructor — it spawns at default field values (position zero) and snaps when real data applies one moment later.
> * Caching plain object references to other entities across ticks — after destruction and id reuse they point at the wrong entity; store `EntitySharedReference` and resolve via `GetEntityById`.
> * Expecting `Destroy()` on the client to remove a synced entity — it is a no-op there by design; only the server (or a local predicted entity) can die.

## Related pages

- [entity-hierarchy.md](entity-hierarchy.md)
- [ownership-and-players.md](ownership-and-players.md)
