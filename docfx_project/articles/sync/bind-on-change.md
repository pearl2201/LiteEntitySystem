---
description: Binding callbacks to SyncVar changes - the two binding forms, the previous-value argument, and choosing timings with BindOnChangeFlags.
---

# Change notifications: BindOnChange

`BindOnChange` attaches a callback to a `SyncVar`, so code runs exactly when the value changes instead of comparing it every frame — with flags deciding which of the several moments a value can change actually fire it.

## When to use this

* Reacting to state arriving from the server: update a label, play a reaction, swap a material.
* Reacting to changes on the entity itself regardless of side — with `ExecuteOnServer` added.

## When not to

* One-shot events that carry their own data (a hit, a shout) — an [RPC](rpc-basics.md); a callback only tells you a value changed.
* Per-frame view updates — read `InterpolatedValue` in the view; callbacks fire at the send rate, not per frame.

## Minimal example

**Lamp.cs**

```csharp
using LiteEntitySystem;

public partial class Lamp : EntityLogic
{
    public SyncVar<bool> IsOn;
    public SyncVar<byte> Brightness;

    public Lamp(EntityParams entityParams) : base(entityParams) { }

    protected override void RegisterRPC(ref RPCRegistrator r)
    {
        base.RegisterRPC(ref r);

        // instance form: callback is a method of this entity
        r.BindOnChange(this, ref IsOn, OnIsOnChanged);

        // static form: no closure over the instance, works for virtual methods
        r.BindOnChange<Lamp, byte>(ref Brightness,
            static (lamp, prevBrightness) => lamp.ApplyBrightness());
    }

    private void OnIsOnChanged(bool wasOn)
    {
        // IsOn already holds the NEW value; wasOn is the previous one
        ApplyBrightness();
    }

    private void ApplyBrightness() { /* update the view */ }
}
```

## How it works

### Where bindings are declared

Bindings live in the `RegisterRPC` override, next to RPC registration, and are set up once per class — like RPCs, they belong to the class, not to an instance. The override must start with `base.RegisterRPC(ref r)`, or base-class bindings are lost.

### The previous-value argument

The callback receives the value the field held *before* the change; the field itself already holds the new one. That asymmetry is deliberate — it lets a callback diff old against new (`if (wasOn != IsOn)`) without keeping a shadow copy.

### Choosing timings

By default a callback fires only when a value arrives from the server (`ExecuteOnSync`). A value can change at other moments too, and each is opt-in:

| Flag | Fires when |
|---|---|
| `ExecuteOnSync` | A state update from the server changes the value (default) |
| `ExecuteOnServer` | Server code changes the value |
| `ExecuteOnPrediction` | Client code changes the value during prediction, including re-simulation |
| `ExecuteOnRollbackReset` | Rollback resets the value to the server's, before `OnRollback` |
| `ExecuteAlways` | All of the above |

Combine what you need: `ExecuteOnSync | ExecuteOnServer` is the usual choice for logic that must run on both sides — the example project's controller uses exactly this to notice possession changes.

### Suppressing a notification

`SetValueWithoutOnSyncNotification(value)` writes a field without triggering its local callback. Use it when a callback would fight the code doing the write — for instance when applying a value the callback itself derived.

## Behavior details

# [Server](#tab/server)

Only `ExecuteOnServer` matters here; without it a server-side write fires nothing. The callback runs synchronously inside the assignment, before the change is serialized.

# [Client](#tab/client)

`ExecuteOnSync` callbacks run as the state is applied, batched after the entity's fields are updated — so a callback can safely read other fields of the same entity and see the same state version.

# [Prediction / rollback](#tab/prediction-rollback)

Predicted writes fire callbacks only with `ExecuteOnPrediction`, and re-simulation makes that repeat for every rollback — keep such callbacks free of one-shot effects, or gate them with `EntityManager.InNormalState`. `ExecuteOnRollbackReset` exists for the opposite need: knowing that a predicted value was just discarded and replaced by the server's.

***

> [!WARNING]
> **Common mistakes**
>
> * Reading the callback argument as the new value — it is the previous one; the field already holds the new value.
> * Expecting a client callback for a value the client itself predicted — that needs `ExecuteOnPrediction`; the default fires only on server sync.
> * Spawning sounds or particles in an `ExecuteOnPrediction` callback — rollback replays them on every correction; gate with `InNormalState`.
> * Assuming a callback fires once per change — with sampling, several intermediate values may collapse into one; and rollback can re-fire prediction callbacks for the same tick.

## Related pages

- [rpc-basics.md](rpc-basics.md)
- [sync-flags.md](sync-flags.md)
