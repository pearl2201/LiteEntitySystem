---
description: What LiteEntitySystem is, what it provides out of the box, and how to tell whether it fits your game.
---

# What is LiteEntitySystem

LiteEntitySystem is a server-authoritative networking library for multiplayer games in pure C# (.NET Standard 2.1): the server simulates the world as a set of entity classes on a fixed tick, and clients receive delta-compressed state while predicting their own actions locally.

It follows the entity model of classic shooter engines: the world is made of entity objects with logic, players possess pawns through controllers, and the server is the single authority. It is not an ECS, and it does not network engine scene objects or components — entities are plain C# classes, which is what lets the same game logic run under Unity (2021.2+, including IL2CPP), Godot, MonoGame, or a plain .NET server without an engine at all.

Out of the box you get: synchronized variables with change notifications, server-to-client RPCs with compile-time checked signatures, a client input system, client-side prediction with rollback, client-side spawn prediction for projectiles, lag compensation, local and remote interpolation, an entity hierarchy (parent/child), controllers and pawns, delta-compressed state and input, LZ4-compressed initial world state, and a transport abstraction with LiteNetLib as the default implementation.

## When to use this

* Fast-paced competitive or action games (FPS/TPS, action RPG) where server authority and client-side prediction are requirements, not options.
* Projects that need one shared game-logic codebase for a headless .NET server and an engine-based client (Unity, Godot, MonoGame).
* Games where cheating matters: clients send only inputs and requests, never state.

## When not to

* If you want to network existing engine scene objects and components directly — LiteEntitySystem entities are plain classes, and views are attached to them manually.
* If your gameplay requires clients to own and freely mutate shared state (relaxed-trust co-op tools, sandboxes) — there is no client-authority mode; the server owns everything.
* If you need full rigidbody physics prediction out of the box — kinematic movement predicts well, but rollback of a full physics engine requires precise control over that engine and is an advanced, engine-dependent setup.

## Minimal example

A synchronized entity looks like this — a plain class with a `SyncVar` field. The server's value is authoritative; every client sees it, and a client can also predict the change locally before the server confirms it.

**Door.cs**

```csharp
using LiteEntitySystem;

public partial class Door : EntityLogic
{
    [SyncVarFlags(SyncFlags.AlwaysRollback)]
    private SyncVar<bool> _isOpen;

    public bool IsOpen => _isOpen;

    public Door(EntityParams entityParams) : base(entityParams) { }

    public void Open()
    {
        _isOpen.Value = true;
    }
}
```

## How it works

### The entity class

`Door` inherits `EntityLogic`, the base class for world entities. It is not a component on a scene object — the library constructs it, on the server when you spawn it and on each client when the entity is first synced. Every entity class must have a constructor taking `EntityParams` and pass it to base.

### The synchronized field

`SyncVar<bool>` is a struct wrapper around the value. Assigning `_isOpen.Value` on the server marks the field as changed; the new value reaches clients inside the next state update. Reading works through `.Value` or implicit conversion, as in the `IsOpen` property.

### Prediction and the rollback flag

There is no `IsServer` guard in `Open()` on purpose: when a client calls it during prediction (for example, the local player interacts with the door), the door opens immediately on that client's screen. The write is a prediction — the server's simulation still decides the real value, and rollback corrects the client if it guessed wrong. The door is a server-owned entity, so its fields are not rolled back by default; `[SyncVarFlags(SyncFlags.AlwaysRollback)]` opts the field into rollback so the predicted value is properly reset to server state on every correction.

## Behavior details

# [Server](#tab/server)
The server runs game logic at a fixed tick rate. Each send cycle it serializes only the fields that changed (delta compression) and sends them unreliably; a newly connected client first receives the full world state, LZ4-compressed, as a reliable baseline. RPCs are delivered in order relative to state updates.
# [Client](#tab/client)
The client applies incoming states, keeps a small buffer of them, and renders remote entities interpolated between the last two states. Its own tick rate speeds up or slows down slightly to stay in sync with the server instead of jumping. The client sends inputs — not state — to the server.
# [Prediction / rollback](#tab/prediction-rollback)
Entities controlled by the local player are simulated ahead of the server (client-side prediction). When a server state arrives, predicted fields are reset to the authoritative values and the entity is re-simulated from stored inputs — a rollback. Game code can detect this mode (`EntityManager.InRollBackState` / `InNormalState`) to keep sounds and effects from replaying, and flags control which fields participate.
***

> [!WARNING]
> **Common mistakes**
>
> * Assigning a whole new struct to a SyncVar field (`_isOpen = new SyncVar<bool>()`) breaks synchronization — only `.Value` may be assigned. Use the Roslyn analyzer shipped in the repository (`AnalyzerBinary`) to catch this at compile time.
> * Predicting SyncVar writes on entities you don't own without marking the field `SyncFlags.AlwaysRollback` — the predicted value is not reset by rollback and can stick on the client until the server happens to change that field again.
> * Creating a manager and never pumping its `Update()` every frame — no ticks run, nothing is sent or received.

## Related pages

- [core-model.md](getting-started/core-model.md)

- [installation.md](getting-started/installation.md)
