---
description: Deciding which fields rollback touches - the ownership default, AlwaysRollback for damage prediction, NeverRollBack, and the rollback hooks.
---

# Controlling rollback per field

By default rollback covers the predicted fields of entities the client owns — two flags widen or narrow that set, and two hooks let an entity react when a reset happens.

## The default and the two overrides

| Field | Rolled back |
|---|---|
| On an owned entity | yes |
| On an owned entity, `NeverRollBack` | no |
| On a remote entity | no |
| On a remote entity, `AlwaysRollback` | yes |

`AlwaysRollback` is what makes cross-entity prediction possible: the client writes a value on an entity it does not own, and rollback keeps that write honest by resetting it to the server's version at every correction.

`NeverRollBack` protects values that must not be rewound even though the entity is predicted — the library uses it internally for ownership and possession references, which are decided by the server and would be nonsense to re-derive from local input.

## Predicting damage

The standard use of `AlwaysRollback`: a shooter that wants its victim's health bar to react on the shooting client immediately, without waiting for the round trip.

**Target.cs**

```csharp
using LiteEntitySystem;

[EntityFlags(EntityFlags.Updateable)]
public partial class Target : PawnLogic
{
    // predicted by whoever shoots this entity, not just by its owner
    [SyncVarFlags(SyncFlags.AlwaysRollback)]
    public SyncVar<byte> Health;

    public Target(EntityParams entityParams) : base(entityParams) { }

    public void ApplyDamage(byte amount)
    {
        // runs on the server (authority) and on the shooter's client (prediction)
        Health.Value = amount >= Health.Value ? (byte)0 : (byte)(Health.Value - amount);

        if (IsServer && Health.Value == 0)
            Destroy();
    }
}
```

The shooting client calls `ApplyDamage` from its predicted `Update`; the health drops instantly on screen. Every correction resets `Health` to the server's value and replays the unconfirmed shots — so a hit the server rejected reverts by itself, without any reconciliation code.

Note the `IsServer` guard on `Destroy()` only: destruction is a server decision, while the value change is shared prediction. Death effects belong behind `EntityManager.InNormalState` for the same reason discussed in [prediction and rollback](prediction-and-rollback.md).

## The hooks

`OnBeforeRollback()` runs before predicted fields are reset — the place to capture anything you need from the pre-reset state. `OnRollback()` runs after the reset, before re-simulation begins: use it to rebuild derived data from the freshly restored values.

Change callbacks can also observe the reset itself: bind with `BindOnChangeFlags.ExecuteOnRollbackReset` to be notified that a predicted value was discarded and replaced by the server's ([details](../sync/bind-on-change.md)).

## Syncable fields

Collections and other [syncable fields](../sync/syncablefield-basics.md) are outside this flag system: a type either derives from `SyncableFieldCustomRollback` and restores itself, or it is untouched by rollback. `SyncList`, `SyncDict`, `SyncHashSet`, `SyncQueue` and the built-in `Childs` do; `SyncString`, `SyncTimer` and `SyncArray` do not. Custom types implement the [four-member contract](../sync/custom-syncablefield.md).

## Non-synchronized state

Rollback only knows about synchronized values. Plain C# fields keep whatever the last replay left them at, which is fine for caches and derived values but wrong for anything gameplay depends on. If a value matters to the simulation, make it a `SyncVar`; if it is derived, recompute it in `OnRollback` rather than accumulating it across ticks.

> [!WARNING]
> **Common mistakes**
>
> * Predicting a change on a remote entity without `AlwaysRollback` — the write is never reset and sticks until the server happens to change that field.
> * `AlwaysRollback` on fields no client predicts — pure overhead: the field is reset and replayed on every correction for nothing.
> * Rebuilding derived data in `OnBeforeRollback` — it runs *before* the reset, so it sees the discarded predicted values; use `OnRollback`.
> * Assuming a plain field is restored — only SyncVars and custom-rollback syncable fields are.

## Related pages

- [interpolation.md](interpolation.md)
- [../sync/sync-flags.md](../sync/sync-flags.md)
