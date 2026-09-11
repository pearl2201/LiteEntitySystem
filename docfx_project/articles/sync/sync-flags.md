---
description: Every SyncFlags value - interpolation, lag compensation, per-owner visibility, rollback control and sync groups - with when to use each.
---

# SyncFlags reference

`[SyncVarFlags(...)]` configures what the library does with a field beyond copying it: whether it interpolates, keeps history, reaches only some players, or takes part in rollback.

## The flags

| Flag | Effect | Typical use |
|---|---|---|
| `None` | Plain synchronized value | Default; ammo counts, phase enums |
| `Interpolated` | Keeps a smoothed companion in `InterpolatedValue` | Position, rotation — anything a view reads per frame |
| `LagCompensated` | Value history is recorded and rewound during hit checks | Position/rotation of shootable entities |
| `OnlyForOwner` | Sent only to the entity's owner | Owner-private data on a shared entity |
| `OnlyForOtherPlayers` | Sent to everyone except the owner | Values the owner derives locally but others must receive |
| `AlwaysRollback` | Participates in rollback even when the entity is not owned | Enemy health, for damage prediction |
| `NeverRollBack` | Excluded from rollback even when the entity is owned | Values that must not be rewound by re-simulation |
| `SyncGroup1`…`SyncGroup5` | Field belongs to a toggleable per-player group | Distance culling, visibility systems |

Flags combine with `|`. Applied to a class instead of a field, the attribute becomes the default for every SyncVar in it; a field-level attribute replaces that default rather than adding to it.

## Minimal example

**Fighter.cs**

```csharp
using LiteEntitySystem;

[EntityFlags(EntityFlags.Updateable)]
public partial class Fighter : PawnLogic
{
    // rendered every frame and shot at → interpolate and keep history
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.LagCompensated)]
    public SyncVar<float> X;

    // other clients predict damage on it → must roll back for them too
    [SyncVarFlags(SyncFlags.AlwaysRollback)]
    public SyncVar<byte> Health;

    // nobody else's business
    [SyncVarFlags(SyncFlags.OnlyForOwner)]
    public SyncVar<byte> Stamina;

    // plain state
    public SyncVar<byte> Team;

    public Fighter(EntityParams entityParams) : base(entityParams) { }
}
```

## How it works

### Interpolation and history

`Interpolated` costs a second copy of the value on the client and requires the type to have a lerp function — built in for primitives and `FloatAngle`, [registered by you](syncvar.md) for engine types. `LagCompensated` costs a ring buffer of `MaxHistorySize` ticks on the server. Both belong on the same handful of fields — the ones describing where something is — and on nothing else.

### Visibility flags

`OnlyForOwner` and `OnlyForOtherPlayers` filter a single field's recipients; they are the field-level counterpart of `EntityFlags.OnlyForOwner`, which hides an entire entity. Use them for owner-private data on an entity everyone can see — a pawn's exact stamina, an aim vector nobody else needs. A field nobody but the owner reads is also a field the server never has to send to the rest.

### Rollback control

By default rollback covers fields of entities the client owns. Two flags override that: `AlwaysRollback` opts a field in even on entities owned by others (the standard choice for health, so a client can predict the damage it deals), `NeverRollBack` opts a field out even on owned entities. The reasoning behind each choice is on [controlling rollback per field](../netcode/rollback-per-field.md).

### Sync groups

`SyncGroup1`…`SyncGroup5` tag a field as belonging to a group the server can switch off per player — the mechanism behind distance culling and visibility systems. RPCs carry the same group tags through `ExecuteFlags`. See [sync groups](sync-groups.md); a field with no group tag is always sent.

## Behavior details

# [Server](#tab/server)

Flags shape packet composition: visibility flags decide recipients per field, group tags are checked against each player's enabled groups, `LagCompensated` fields get their history written every tick. `Interpolated` costs nothing here — the server holds only the authoritative value.

# [Client](#tab/client)

`Interpolated` maintains the smoothed companion; group-disabled fields simply stop updating (the last received value stays); `OnlyForOwner` fields on remote entities never arrive at all and hold their defaults — never make client logic depend on such a field of another player's entity.

# [Prediction / rollback](#tab/prediction-rollback)

Ownership plus `AlwaysRollback`/`NeverRollBack` determine the rolled-back set. Fields outside it keep whatever the last state delivered, without being rewound or re-derived.

***

> [!WARNING]
> **Common mistakes**
>
> * A field-level `[SyncVarFlags]` on a class that also has one — the field's attribute replaces the class default, it does not merge; restate the flags you still need.
> * `Interpolated` on a value that snaps (health, ammo, a mode enum) — you get a meaningless midpoint between two states while the view lags behind the truth.
> * `LagCompensated` on everything — each such field costs history memory every tick; mark only what hit checks actually test.
> * Predicting damage on other players' entities without `AlwaysRollback` — predicted writes on non-owned entities are never reset and can stick until the server changes that field again.

## Related pages

- [bind-on-change.md](bind-on-change.md)
- [sync-groups.md](sync-groups.md)
