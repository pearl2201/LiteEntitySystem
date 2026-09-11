---
description: The Unity view patterns - entity-owned GameObjects, the proxy MonoBehaviour, local vs remote prefabs, and effects that outlive entities.
---

# Attaching Unity views

Entities are plain classes, so something has to put them on screen: the established pattern is an entity-owned GameObject created in `OnConstructed`, a proxy MonoBehaviour that mirrors interpolated state every frame, and external subscriptions for the pieces that belong to the player rather than the entity (camera, UI).

## When to use this

* Any entity a Unity client renders — pawns, projectiles, pickups.
* Physics-queryable views: the proxy is also how raycast hits resolve back to entities.

## When not to

* Effects that must outlive their entity (death explosion, hit sparks) — pooled MonoBehaviours triggered from RPC handlers or `OnDestroy`, not entity views.
* Headless server builds — no view code runs there at all; keep it behind `IsClient` or in a client-only assembly.

## Minimal example

**ViewPlayer.cs**

```csharp
using LiteEntitySystem;
using UnityEngine;

[EntityFlags(EntityFlags.Updateable)]
public partial class ViewPlayer : PawnLogic
{
    [SyncVarFlags(SyncFlags.Interpolated)]
    public SyncVar<Vector2> Position;

    public GameObject UnityObject { get; private set; }

    public ViewPlayer(EntityParams entityParams) : base(entityParams) { }

    protected override void OnConstructed()
    {
        if (!IsClient)
            return;
        UnityObject = new GameObject($"Player_{Id}");
        UnityObject.AddComponent<EntityProxy>().Attached = this;
    }

    protected override void OnDestroy()
    {
        if (UnityObject != null)
            Object.Destroy(UnityObject);
    }
}

public class EntityProxy : MonoBehaviour
{
    public ViewPlayer Attached;

    private void Update() =>
        transform.position = Attached.Position.InterpolatedValue;
}
```

**Camera for the local player (external reaction)**

```csharp
_entityManager.GetEntities<ViewPlayer>().SubscribeToConstructed(p =>
{
    if (p.IsLocalControlled)
        AttachCamera(p.UnityObject);
}, callOnExisting: true);
```

## How it works

### Entity-owned views

The entity creates its GameObject in `OnConstructed` (synced values are applied — it spawns in the right place) and destroys it in `OnDestroy`. Prefab-based views load and `Instantiate` the same way the example's projectile does; naming objects with the entity `Id` pays off in the hierarchy window. The view's lifetime is exactly the entity's lifetime, with no bookkeeping.

### The proxy

The proxy MonoBehaviour has two jobs. Forward: mirror entity state into the transform every render frame, always from `InterpolatedValue`. Backward: physics — the example's `PlayerProxy` sits on the collider object, so a raycast hit resolves to `hit.TryGetComponent<PlayerProxy>()` → the entity behind it. One component, both directions of the entity↔engine bridge.

### Local vs remote

`IsLocalControlled` splits presentation: the example instantiates `ClientPlayerView` (camera rig) for the owner and `RemotePlayerView` (name tag, health label) for everyone else — via `SubscribeToConstructed`, because the camera belongs to the player, not to any single pawn's class. Sync-group changes plug in the same way: the example toggles `UnityObject.SetActive` in `OnSyncGroupsChanged` when the server culls a player's data by distance.

### Where view code lives

Two options. `IsClient` guards inside a shared class — simple, used by the example. Or a client subclass under the [side-variant registration](../getting-started/registering-entity-types.md): all Unity code in `ClientPlayer : BasePlayer` in a client-only assembly, and the server build never references UnityEngine types at all. The second scales better for dedicated-server projects.

### Effects that outlive entities

A death explosion must play after its entity is gone, so it cannot be the entity's view. The example's pattern: pooled effect MonoBehaviours (`GamePool<T>`), spawned from RPC handlers and `OnDestroy` — fire-and-forget engine objects, invisible to the entity system.

## Behavior details

# [Server](#tab/server)

No views, ever — `OnConstructed` runs but the `IsClient` guard (or the absence of the client assembly) keeps it logic-only. A dedicated build with side-variant classes contains no UnityEngine view code to strip.

# [Client](#tab/client)

Views follow the entity lifecycle exactly: constructed after data applies, destroyed with the entity, reattached in `OnParentChanged` when the [entity tree](parent-child.md) changes. The proxy reads interpolated values, so views are smooth regardless of send rate.

# [Prediction / rollback](#tab/prediction-rollback)

Rollback never touches views: lifecycle hooks don't re-fire and the proxy keeps reading `InterpolatedValue`, which absorbs small corrections smoothly. Only effect-spawning code inside `Update` needs the `InNormalState` gate covered in [update-flags.md](update-flags.md).

***

> [!WARNING]
> **Common mistakes**
>
> * The proxy reading `.Value` instead of `.InterpolatedValue` — movement stutters at the send rate; interpolation only exists on fields marked `SyncFlags.Interpolated`.
> * Creating the view in the entity constructor — synced values aren't applied yet, the object spawns at the origin and snaps.
> * No `OnDestroy` cleanup — orphaned GameObjects accumulate every respawn.
> * Gameplay logic in the view's MonoBehaviour `Update` — the view is a read-only mirror; logic belongs in the entity's `Update`, where prediction and rollback can see it.

## Related pages

- [update-flags.md](update-flags.md)
- [finding-entities.md](finding-entities.md)
