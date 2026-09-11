---
description: Why server-side hit checks miss without rewinding, and how EnableLagCompensationForOwner makes the server check against what the shooter saw.
---

# Lag compensation

Every client renders remote players slightly in the past (that is what interpolation is), so when a player fires at what they see, the server — which lives in the present — would test the shot against positions the shooter never saw; lag compensation rewinds the relevant entities to the shooter's view for the duration of the hit check.

## When to use this

* Instant hit checks against other players: hitscan weapons, melee swings, interaction traces.
* Per-tick collision checks of fast movers — the example project wraps even its projectile raycast in lag compensation.

## When not to

* Checks against static geometry — walls don't move; rewinding is about other players' entities.
* Anything about the shooter's own entities — lag compensation rewinds *other* players' entities relative to the shooter; the owner is never rewound.

## Minimal example

A pawn whose position participates in lag compensation, and a shot that uses it:

**ShooterPlayer.cs**

```csharp
using LiteEntitySystem;

[EntityFlags(EntityFlags.Updateable)]
public partial class ShooterPlayer : PawnLogic
{
    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.LagCompensated)]
    public SyncVar<float> X;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.LagCompensated)]
    public SyncVar<float> Y;

    public ShooterPlayer(EntityParams entityParams) : base(entityParams) { }

    public void Shoot()
    {
        EnableLagCompensationForOwner();
        // run the hit query here: every other player's lag-compensated
        // fields temporarily hold the values this player saw on screen
        DisableLagCompensationForOwner();
    }

    protected override void OnLagCompensationStart()
    {
        // this entity was just rewound: push X/Y into your physics
        // engine (collider/body position) so raycasts see the old pose
    }

    protected override void OnLagCompensationEnd()
    {
        // values are restored: push X/Y back into the physics engine
    }
}
```

## How it works

### Marking fields

Only fields with `SyncFlags.LagCompensated` keep a history and get rewound — typically position and rotation. The server records them every tick into a ring buffer of `MaxHistorySize` ticks (a `ServerEntityManager` constructor parameter, default 32).

### The rewind scope

`EnableLagCompensationForOwner()` rewinds the lag-compensated fields of all *other* players' entities to the time this entity's owner was looking at — reconstructed from which two states the client was interpolating between at that input's tick. `DisableLagCompensationForOwner()` restores the present. Treat the pair as a scope: enable, query, disable, in the same tick.

### The physics hooks

Rewinding a `SyncVar` moves numbers, not colliders. If your hit query goes through a physics engine, each rewound entity gets `OnLagCompensationStart` / `OnLagCompensationEnd` — push the rewound values into the physics body there and restore them after, exactly as the example project does with its 2D rigidbodies.

### Choosing the history size

`MaxHistorySize` bounds how far back a shot can be honored — 32 ticks is about half a second at 60 ticks per second, a full second at 30. This is a game-design dial with no correct value: a longer history produces more "I ran behind a wall and still died", a shorter one more "I shot straight at him and missed". Tune it per game.

## Behavior details

# [Server](#tab/server)
History is written for every lag-compensated entity each tick. During the enable/disable window, reads of lag-compensated fields return the rewound values; everything else is untouched. If the requested time has already left the history window, compensation for that shot is skipped and a miss is logged.
# [Client](#tab/client)
In normal play, `EnableLagCompensationForOwner()` is a no-op on the client — there is nothing to rewind, the client already sees its own screen. The call is still safe (and correct) to leave in shared shooting code.
# [Prediction / rollback](#tab/prediction-rollback)
During rollback re-simulation the same call *does* work on the client: hit checks re-run under the same rewound conditions the server used, which is what keeps predicted hits (with `SyncFlags.AlwaysRollback` fields like health) consistent with the server's verdict.
***

> [!WARNING]
> **Common mistakes**
>
> * Forgetting `SyncFlags.LagCompensated` on the fields being tested — the enable call succeeds but those fields simply never rewind, and shots miss by exactly the interpolation delay.
> * Enabling without disabling — the rewound state leaks into the rest of the tick and corrupts movement and later checks. Keep the pair tight around the query.
> * Rewinding numbers but raycasting stale colliders — without pushing values to the physics engine in `OnLagCompensationStart`/`End`, the raycast still hits present-time poses.

## Related pages

- [adding-a-player.md](adding-a-player.md)

- [example-project-tour.md](example-project-tour.md)
