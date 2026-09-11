---
description: Player movement end-to-end - the input struct, a PawnLogic body, a HumanControllerLogic that polls and applies input, and possession on join.
---

# Adding a player: controller and pawn

Player control is split across two entities: the *pawn* (`PawnLogic`) is the body that moves in the world, the *controller* (`HumanControllerLogic`) reads the player's input and drives the pawn — the same split as in classic shooter engines, and the part of the library that makes prediction work automatically.

## When to use this

* Any player-driven thing: a character, a vehicle, a spectator camera — as a pawn possessed by that player's controller.
* One `HumanControllerLogic` per connected player; it survives pawn deaths and can possess a new pawn.

## When not to

* Server-driven AI — that is `AiControllerLogic`, registered on the server only.
* World objects nobody steers directly (the crystal from [first-synced-entity.md](first-synced-entity.md)) — plain `EntityLogic`, no controller involved.

## Minimal example

Three pieces: an input struct, a pawn, a controller.

**PlayerControl.cs**

```csharp
using System;
using LiteEntitySystem;

[Flags]
public enum MoveKeys : byte
{
    Up = 1, Down = 1 << 1, Left = 1 << 2, Right = 1 << 3
}

public struct PlayerInput
{
    public MoveKeys Keys;
}

[EntityFlags(EntityFlags.Updateable)]
public partial class MyPlayer : PawnLogic
{
    [SyncVarFlags(SyncFlags.Interpolated)]
    public SyncVar<float> X;

    [SyncVarFlags(SyncFlags.Interpolated)]
    public SyncVar<float> Y;

    private float _moveX, _moveY;
    private const float Speed = 3f;

    public MyPlayer(EntityParams entityParams) : base(entityParams) { }

    public void SetMove(float x, float y)
    {
        _moveX = x;
        _moveY = y;
    }

    protected override void Update()
    {
        base.Update(); // lets the controller apply input first — do not remove
        X.Value += _moveX * Speed * EntityManager.DeltaTimeF;
        Y.Value += _moveY * Speed * EntityManager.DeltaTimeF;
    }
}

public class MyPlayerController : HumanControllerLogic<PlayerInput, MyPlayer>
{
    public MyPlayerController(EntityParams entityParams) : base(entityParams) { }

    protected override void VisualUpdate()
    {
        ref PlayerInput input = ref ModifyPendingInput();
        // poll your engine here, e.g.:
        // if (Input.GetKey(KeyCode.W)) input.Keys |= MoveKeys.Up;
    }

    protected override void BeforeControlledUpdate()
    {
        MoveKeys keys = CurrentInput.Keys;
        ControlledEntity?.SetMove(
            ((keys & MoveKeys.Right) != 0 ? 1f : 0f) - ((keys & MoveKeys.Left) != 0 ? 1f : 0f),
            ((keys & MoveKeys.Up) != 0 ? 1f : 0f) - ((keys & MoveKeys.Down) != 0 ? 1f : 0f));
    }
}
```

On the server, possession happens at join — the same two lines as in [starting-a-server.md](starting-a-server.md):

**On the server**

```csharp
var pawn = _entityManager.AddEntity<MyPlayer>();
_entityManager.AddController<MyPlayerController>(player, pawn);
```

## How it works

### The input struct

Input is one unmanaged struct per tick. It is sent unreliably and redundantly (resent until the server confirms it) and delta-compressed against the previous tick, so keep it small and flat — flags in one enum, floats for analog axes. If a clean input isn't all zeroes for your game, override `GetDefaultInput()`.

### The pawn

The pawn is a normal entity plus possession. Calling `base.Update()` first is what invokes the controller's `BeforeControlledUpdate()` on this tick, so input is applied before movement code runs. Everything else — SyncVars, interpolation, views — works exactly as on the [first entity page](first-synced-entity.md).

`BeforeControlledUpdate` is a convenience, not the only way in. The pawn can equally pull input itself, at the cost of a cast to the concrete controller:

**Alternative: pull instead of push**

```csharp
protected override void Update()
{
    if (Controller is MyPlayerController pc)
    {
        MoveKeys keys = pc.CurrentInput.Keys;
        // apply movement directly from keys
    }
}
```

### The controller

`VisualUpdate()` runs every render frame on the owning client and is the only right place to poll engine input: fill the struct returned by `ModifyPendingInput()`. At the next logic tick the library turns the pending input into `CurrentInput`, stores it in history and sends it. `BeforeControlledUpdate()` then translates `CurrentInput` into pawn commands — it runs on both the owning client (prediction) and the server (authority), with the input for the tick being simulated. The `HumanControllerLogic<TInput, T>` variant adds the typed `ControlledEntity` shortcut.

### Possession and ownership

`AddController<T>(player, pawn)` creates the controller owned by that player and starts control. Possession propagates ownership: the pawn becomes locally controlled on that player's client, which automatically makes it predicted there. The controller itself is marked "only for owner" — other clients never receive it, so all code that reads input lives outside the world other players see.

### The round trip of one keypress

1. Owning client, render frame: `VisualUpdate` writes the key into the pending input.
2. Owning client, next logic tick: pending becomes `CurrentInput`; the tick's input is stored and sent; `BeforeControlledUpdate` + pawn `Update` move the pawn immediately — that is the prediction.
3. Server, the tick with the same number: the received input is applied, the identical `BeforeControlledUpdate` + `Update` path runs — that is the authority.
4. Owning client, on the next server state: predicted fields reset, stored inputs re-simulate the pawn; if server and prediction agreed, nothing visibly changes.
5. Everyone else sees the pawn's SyncVars interpolated, like any other entity.

## Behavior details

# [Server](#tab/server)
Incoming inputs are buffered per player and applied on the matching tick. If a tick's input hasn't arrived (loss, jitter), the previously applied input remains in effect — a briefly held key is a smoother guess than a zeroed one. The elastic client clock keeps a small reserve of inputs buffered server-side.
# [Client](#tab/client)
Only the owning client has the controller. Its pawn is simulated locally every tick without waiting for the server; `X`/`Y` here are predictions. Remote players' pawns arrive as ordinary synced entities and are interpolated — their controllers don't exist on this client.
# [Prediction / rollback](#tab/prediction-rollback)
During rollback the library replays stored inputs: for each re-simulated tick the controller's `CurrentInput` is loaded from history and `BeforeControlledUpdate` runs again, so the pawn's `Update` sees exactly what it saw the first time. This is automatic — but it is why input polling must never happen in `Update` or `BeforeControlledUpdate`.

Re-simulation also means movement code runs multiple times for the same tick. Pure state math is safe to repeat; one-shot side effects (footstep sounds, dust particles) are not — gate them with `EntityManager.InNormalState` so they fire only during the original simulation, not on every correction.
***

> [!WARNING]
> **Common mistakes**
>
> * Relying on `BeforeControlledUpdate` but overriding the pawn's `Update()` without `base.Update()` — the callback never runs, `CurrentInput` never reaches the pawn, and movement silently dies on server and client alike. (Not an issue if the pawn pulls input from `Controller` itself.)
> * Polling engine input anywhere except `VisualUpdate` — ticks and render frames are not 1:1 (keypresses between ticks get lost), and during rollback `BeforeControlledUpdate` re-runs with historical input, where a live poll corrupts the re-simulation.
> * A fat input struct (reference types won't compile; big structs hurt) — it travels every tick, redundantly; keep it to key flags and a few floats.

## Related pages

- [lag-compensation.md](lag-compensation.md)

- [starting-a-server.md](starting-a-server.md)
