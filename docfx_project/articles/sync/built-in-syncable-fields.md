---
description: The ready-made SyncableField types shipped with the library - what each is for and which ones take part in rollback.
---

# Built-in syncable fields

The `LiteEntitySystem.Extensions` namespace ships syncable types for the common cases, so most entities never need a custom one. This page is a map; the API reference has the exact members of each type.

## What is available

| Type | For |
|---|---|
| `SyncString` | Text, with a `ValueChanged` event |
| `SyncList<T>` | A growable list of unmanaged values |
| `SyncArray<T>` | A fixed-length array, resizable as a whole |
| `SyncFixedArray<T>` | A fixed-capacity array of comparable values |
| `SyncDict<TKey,TValue>` | Key/value pairs of unmanaged types |
| `SyncHashSet<T>` | A set of unmanaged values |
| `SyncQueue<T>` | FIFO of unmanaged values |
| `SyncTimer` | A countdown that ticks with your logic (`Update(dt)`, `IsTimeElapsed`, `Progress`) |
| `SyncPongTimer` | A timer that bounces between its bounds |
| `SyncStateMachine<T>` | An enum-keyed state machine with `OnEnter`/`OnUpdate`/`OnExit` per state |
| `SyncNetSerializable<T>` | A value serialized through LiteNetLib's `INetSerializable` |
| `SyncSpanSerializable<T>` | A value serialized through [`ISpanSerializable`](rpc-payloads.md) |
| `SyncScriptableObject<T>` | A Unity `ScriptableObject` that is also `INetSerializable` |
| `JsonSyncableField<T>` | A Unity object synchronized as JSON |
| `SyncSquare<T>` | A two-dimensional grid of unmanaged values |

Element types of the collections must be `unmanaged` — the same constraint as [`SyncVar<T>`](syncvar.md).

## Using them

Declare as `readonly` fields, mutate on the server, read anywhere — the rules of [SyncableField basics](syncablefield-basics.md) apply unchanged:

**Inventory.cs**

```csharp
using LiteEntitySystem;
using LiteEntitySystem.Extensions;

public partial class Inventory : EntityLogic
{
    public readonly SyncString OwnerName = new();
    public readonly SyncList<ushort> Items = new();
    public readonly SyncStateMachine<LockState> State = new();

    public Inventory(EntityParams entityParams) : base(entityParams)
    {
        State.Add(LockState.Locked, new StateCalls { OnEnter = PlayLockSound });
    }

    private void PlayLockSound() { }
}

public enum LockState : byte { Locked, Open }
```

## Rollback support

Types built on `SyncableFieldCustomRollback` restore their server-confirmed contents at every rollback and re-apply predicted edits during re-simulation: `SyncList`, `SyncDict`, `SyncHashSet`, `SyncQueue` (and the built-in `Childs` of every `EntityLogic`).

Everything else — `SyncString`, `SyncTimer`, `SyncArray`, the serializable wrappers — is outside prediction: its contents are whatever the server last sent. That is fine for data a client never modifies predictively, and a reason to prefer a rollback-aware collection when it does.

> [!WARNING]
> **Common mistakes**
>
> * Editing a non-rollback syncable field from predicted code — the change is never reverted or replayed and drifts out of step with the server.
> * A managed element type in a collection (`SyncList<string>`) — elements must be `unmanaged`; synchronize text with `SyncString`, or ids with a lookup.
> * Rewriting a whole collection every tick — these types send per-operation calls; a full rewrite each tick sends the whole thing each tick.

## Related pages

- [custom-syncablefield.md](custom-syncablefield.md)
- [syncablefield-basics.md](syncablefield-basics.md)
