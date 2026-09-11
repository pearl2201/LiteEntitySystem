---
description: What a SyncableField is, how it differs from SyncVar, why it must be readonly, and how late joiners get its full contents.
---

# SyncableField basics

A `SyncableField` synchronizes data that doesn't fit in a fixed-size value — strings, collections, timers — by shipping changes as internal RPCs instead of as a delta of raw bytes.

## When to use this

* Data with variable size or internal structure: text, lists, dictionaries, state machines.
* Ready-made types cover most needs — strings, collections, timers and state machines are [already shipped](built-in-syncable-fields.md).

## When not to

* Fixed-size unmanaged values — a [`SyncVar<T>`](syncvar.md) is cheaper and interpolatable.
* Data that changes wholesale every tick — the incremental model pays off for edits, not for full rewrites.

## Minimal example

**Locker.cs**

```csharp
using LiteEntitySystem;
using LiteEntitySystem.Extensions;

public partial class Locker : EntityLogic
{
    // syncable fields are readonly instance fields
    public readonly SyncString Label = new();
    public readonly SyncList<ushort> ItemIds = new();
    private readonly SyncTimer _relockTimer;

    public Locker(EntityParams entityParams) : base(entityParams)
    {
        // constructing in the constructor works just as well
        _relockTimer = new SyncTimer(30f);
    }

    // server-side edits
    public void Rename(string label) => Label.Value = label;
    public void Store(ushort itemId) => ItemIds.Add(itemId);

    protected override void Update()
    {
        _relockTimer.Update(EntityManager.DeltaTimeF);
    }
}
```

## How it works

### Not a value, an object

A `SyncVar` is a struct holding one value; a `SyncableField` is a class instance living inside the entity, with its own methods and its own internal RPCs. `Label.Value = "Ammo"` doesn't mark a field dirty — it sends a "set string" call to the clients that may see this entity. Collections send "add", "remove", "clear" calls rather than resending the whole contents.

### The readonly rule

Declare syncable fields as `readonly`. Create them where you prefer — inline at the declaration or in the entity constructor; both run before the library binds each instance to its entity. What must not happen is replacing the instance afterwards (`Label = new SyncString()`): the new object is not bound and its edits go nowhere. `readonly` makes that impossible to write by accident — the same discipline as the `.Value`-only rule for SyncVars, enforced by the compiler instead of the analyzer.

### Late joiners and full state

Since edits are incremental, a client arriving later would miss everything sent before it — so each syncable type implements `OnSyncRequested`, called when a new client needs the entity's full state, and re-sends the current contents in one go. Built-in types handle this for you; a [custom syncable](custom-syncablefield.md) must implement it, or its data appears empty to every client that joins after the edits.

### Flags and visibility

`[SyncVarFlags(...)]` works on syncable fields too, but it means something slightly different: the visibility flag is translated into the delivery rule for **every internal RPC the field sends**.

| Flag on the field | Every internal call is sent to |
|---|---|
| *(none)* | all players who can see the entity |
| `OnlyForOwner` | the entity's owner only |
| `OnlyForOtherPlayers` | everyone except the owner |

So `[SyncVarFlags(SyncFlags.OnlyForOwner)] private readonly SyncList<ushort> _questLog = new();` gives the owner a private list: other clients hold an empty instance and never receive its edits — the same idea as an owner-only `SyncVar`, applied to the whole field. Sync-group tags route the field's calls like any other RPC. Interpolation and lag compensation are meaningless here and are ignored.

The rule is fixed at binding time, so it applies uniformly to all calls of that field — there is no per-operation targeting.

### Rollback

Plain `SyncableField` types are outside prediction: their state is whatever the server last sent. Types whose contents a client may modify predictively derive from `SyncableFieldCustomRollback` and implement the reset themselves — `SyncList`, `SyncDict`, `SyncHashSet`, `SyncQueue` and the built-in `Childs` do this; `SyncString`, `SyncTimer` and `SyncArray` do not.

## Behavior details

# [Server](#tab/server)

Every mutating call produces an internal RPC to the recipients allowed by the field's visibility flags, ordered with state like any other RPC. Reads are free — a syncable field's contents are ordinary memory on the server.

# [Client](#tab/client)

Contents change only when those internal RPCs arrive; between them the field is stable. Client-side edits of a plain syncable field are local-only and will be overwritten (or simply diverge) — treat mutation as a server operation unless the type supports custom rollback.

# [Prediction / rollback](#tab/prediction-rollback)

`SyncableFieldCustomRollback` types restore their server-confirmed contents at the start of every rollback and re-apply predicted edits during re-simulation. Plain syncable types are untouched by rollback entirely.

***

> [!WARNING]
> **Common mistakes**
>
> * Assigning a new instance to a syncable field — the replacement is not bound to the entity and its edits never sync; declare the field `readonly`.
> * Creating a syncable field lazily (`?? new SyncString()` on first use) — binding happens once, right after the entity is constructed; the object must already exist by then.
> * Expecting `SyncFlags.Interpolated` or `LagCompensated` to do something on a syncable field — only visibility flags and sync groups apply.
> * Reading an `OnlyForOwner` syncable field on a non-owning client — that client never received a single edit, so the field is empty rather than merely stale.
> * Mutating a plain syncable field on the client and expecting it to hold — only custom-rollback types are prediction-aware.

## Related pages

- [built-in-syncable-fields.md](built-in-syncable-fields.md)
- [custom-syncablefield.md](custom-syncablefield.md)
