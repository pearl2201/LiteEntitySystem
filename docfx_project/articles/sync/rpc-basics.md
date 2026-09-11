---
description: Declaring and executing RPCs - static handles, CreateRPCAction registration, ExecuteRPC, and every ExecuteFlags combination.
---

# RPC basics

An RPC is a one-shot call the server sends to clients, ordered with the state stream: a static handle declares it, `RegisterRPC` binds it to a method, and `ExecuteRPC` fires it.

## When to use this

* Events that happen at a moment and carry no lasting state: a shot, a hit, a jump sound, a level-up flash.
* Anything a late joiner should *not* replay — state is for "what is it now", RPCs for "what just happened".

## When not to

* Continuous values — a [`SyncVar`](syncvar.md); a late joiner needs the current value, not the history of changes.
* Client-to-server communication — RPCs only travel server-to-client; use the input struct or [requests](client-requests.md).
* Reacting to a value change — [`BindOnChange`](bind-on-change.md) already gives you that without a separate message.

## Minimal example

**Grenade.cs**

```csharp
using LiteEntitySystem;

[EntityFlags(EntityFlags.Updateable)]
public partial class Grenade : EntityLogic
{
    private static RemoteCall _fuseHissRpc;
    private static RemoteCall<byte> _explodeRpc;

    public Grenade(EntityParams entityParams) : base(entityParams) { }

    protected override void RegisterRPC(ref RPCRegistrator r)
    {
        base.RegisterRPC(ref r);

        // instance form: binds a method of this entity
        r.CreateRPCAction(this, OnFuseHiss, ref _fuseHissRpc, ExecuteFlags.SendToAll);

        // static form: dispatches through the type, so subclass overrides win
        r.CreateRPCAction<Grenade, byte>(
            static (grenade, power) => grenade.OnExplode(power),
            ref _explodeRpc,
            ExecuteFlags.SendToAll | ExecuteFlags.ExecuteOnServer);
    }

    // server-side triggers
    public void StartFuse() => ExecuteRPC(_fuseHissRpc);
    public void Explode(byte power) => ExecuteRPC(_explodeRpc, power);

    private void OnFuseHiss() { /* clients: start the hiss loop */ }

    protected virtual void OnExplode(byte power)
    {
        // runs on clients AND on the server (ExecuteOnServer):
        // shared damage/effect code lives here
    }
}
```

## How it works

### Static handles

An RPC handle is a `static` field because registration happens once per class, not per instance: the handle stores the method's slot in the class's RPC table and holds no reference to any entity. `ExecuteRPC` supplies the instance. Declaring the handle non-static wastes memory per entity and gains nothing.

### Registration: two forms

Both live inside the `RegisterRPC` override, which must begin with `base.RegisterRPC(ref r)` — the library logs an error naming the class if that call is missing, and base-class RPCs stop working.

**Instance form** — `CreateRPCAction(this, Method, ref handle, flags)`. Shortest to write; binds the method of the class where you call it. Use it for non-virtual handlers.

**Static form** — `CreateRPCAction<TEntity>(static e => e.Method(), ref handle, flags)`, or `CreateRPCAction<TEntity, T>(static (e, value) => e.Method(value), ref handle, flags)` with a payload. Dispatches through the entity type, so a `virtual` handler overridden in a subclass runs the override. This is the same instance-vs-static split as in [`BindOnChange`](bind-on-change.md), and for the same reason.

Since the base class registers the RPC once for the whole hierarchy, a virtual handler must be registered in the static form — otherwise every subclass executes the base implementation.

### Firing

`ExecuteRPC(handle)` — or `ExecuteRPC(handle, value)` for payload variants — sends the call to the players selected by the flags. Only the server sends; on a client the same call does nothing unless `ExecuteOnPrediction` applies (below). Payload forms are covered in [RPC payloads](rpc-payloads.md).

### ExecuteFlags

| Flag | Effect |
|---|---|
| `SendToOwner` | Deliver to the entity's owner |
| `SendToOther` | Deliver to everyone except the owner |
| `SendToAll` | Both of the above |
| `ExecuteOnServer` | Also run the method locally on the server when firing |
| `ExecuteOnPrediction` | Also run locally on the owning client during prediction |
| `All` | Everything above |
| `SyncGroup1`…`SyncGroup5` | Tag the RPC into a toggleable [sync group](sync-groups.md) |

`ExecuteOnServer` is how one method serves as shared logic for both sides. `ExecuteOnPrediction` makes the owning client run the effect immediately at prediction time instead of waiting for the server's copy — pair it with `SendToOther` so the owner does not also receive the network version and fire twice (the example project's shooting RPCs use exactly `ExecuteOnPrediction | SendToOther`).

### Delivery and ordering

RPCs behave as reliable ordered messages: every call reaches every selected player exactly once, in the order it was fired, and in order with state updates — a call fired on the tick a field changed arrives with that field's new value applied. A dropped packet delays the calls behind it but never loses them.

The one exception is a session boundary. A client that reconnects starts from a fresh baseline and receives no calls fired before it: RPCs are events of a live connection, not history. Anything a returning player must know is state, not an RPC — the same rule as for late joiners.

## Behavior details

# [Server](#tab/server)

`ExecuteRPC` enqueues the call for each selected player and, with `ExecuteOnServer`, invokes the method locally right away. RPCs on AI controllers are dropped — nothing about them is networked. With no players connected, nothing is enqueued.

# [Client](#tab/client)

Handlers run as the containing state is processed, in the order the server fired them, with that state's field values already applied. A client cannot originate an RPC.

# [Prediction / rollback](#tab/prediction-rollback)

With `ExecuteOnPrediction`, the owning client runs the handler at prediction time — and re-simulation does not repeat it, so a predicted effect is not duplicated by rollback. Inside handlers, avoid spawning predicted entities: `AddPredictedEntity` called from a server-to-client RPC is rejected and logged.

***

> [!WARNING]
> **Common mistakes**
>
> * Forgetting `base.RegisterRPC(ref r)` — base-class RPCs and bindings are silently lost; the library logs an error with the class name.
> * `ExecuteOnPrediction` together with `SendToAll` — the owner runs the handler twice, once predicted and once from the network; use `SendToOther` for the network half.
> * Registering a `virtual` handler in the instance form — registration happens once per hierarchy, so every subclass ends up running the base implementation; use the static form for virtual handlers.
> * Using RPCs to carry state — neither a late joiner nor a reconnecting player receives past calls; anything they must know belongs in a `SyncVar`.
> * Calling `ExecuteRPC` from client code and expecting it to reach the server — RPCs are one-directional; the client's channels are input and [requests](client-requests.md).

## Related pages

- [rpc-payloads.md](rpc-payloads.md)
- [client-requests.md](client-requests.md)
