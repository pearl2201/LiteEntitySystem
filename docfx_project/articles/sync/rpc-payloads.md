---
description: The four RPC payload forms - no argument, unmanaged struct, span of values, and ISpanSerializable for variable-length data.
---

# RPC payloads

An RPC carries either nothing, one unmanaged value, a span of unmanaged values, or a custom-serialized payload — one handle type per case, with the same registration and firing API.

## The four forms

| Handle | Payload | Use for |
|---|---|---|
| `RemoteCall` | none | Pure signals: "reload finished", "start the effect" |
| `RemoteCall<T>` | one `unmanaged` value | Fixed data: a struct of positions, a damage amount |
| `RemoteCallSpan<T>` | `ReadOnlySpan<T>` of unmanaged values | Variable-count uniform data: hit points, a path |
| `RemoteCallSerializable<T>` | `T : ISpanSerializable` | Anything else: strings, mixed or optional fields |

## Minimal example

**Announcer.cs**

```csharp
using LiteEntitySystem;

public struct DamageInfo
{
    public ushort Amount;
    public byte HitZone;
}

public struct KillMessage : ISpanSerializable
{
    public string Killer;
    public string Victim;

    public int MaxSize =>
        SpanWriter.GetMaxStringSize(Killer) + SpanWriter.GetMaxStringSize(Victim);

    public void Serialize(ref SpanWriter writer)
    {
        writer.Put(Killer);
        writer.Put(Victim);
    }

    public void Deserialize(ref SpanReader reader)
    {
        reader.Get(out Killer);
        reader.Get(out Victim);
    }
}

public partial class Announcer : EntityLogic
{
    private static RemoteCall<DamageInfo> _damageRpc;
    private static RemoteCallSpan<int> _comboRpc;
    private static RemoteCallSerializable<KillMessage> _killRpc;

    public Announcer(EntityParams entityParams) : base(entityParams) { }

    protected override void RegisterRPC(ref RPCRegistrator r)
    {
        base.RegisterRPC(ref r);
        r.CreateRPCAction(this, OnDamage, ref _damageRpc, ExecuteFlags.SendToOwner);
        r.CreateRPCAction(this, OnCombo, ref _comboRpc, ExecuteFlags.SendToAll);
        r.CreateRPCAction(this, OnKill, ref _killRpc, ExecuteFlags.SendToAll);
    }

    // server side
    public void ReportDamage(ushort amount, byte zone) =>
        ExecuteRPC(_damageRpc, new DamageInfo { Amount = amount, HitZone = zone });

    public void ReportCombo(ReadOnlySpan<int> scores) => ExecuteRPC(_comboRpc, scores);

    public void ReportKill(string killer, string victim) =>
        ExecuteRPC(_killRpc, new KillMessage { Killer = killer, Victim = victim });

    private void OnDamage(DamageInfo info) { /* owner's HUD */ }
    private void OnCombo(ReadOnlySpan<int> scores) { /* the span is valid only here */ }
    private void OnKill(KillMessage message) { /* kill feed */ }
}
```

## How it works

### Unmanaged payloads

`RemoteCall<T>` copies the value's bytes as-is, so `T` must be `unmanaged`: primitives, enums, or structs built from them. Group related arguments into a small struct rather than firing several RPCs — one call is one ordered message, several are several. Keep it compact; every byte ships to every recipient.

### Span payloads

`RemoteCallSpan<T>` sends a variable number of unmanaged elements. The handler receives a `ReadOnlySpan<T>` that is valid **only for the duration of the call** — it points into the receive buffer. Copy what you need to keep (`ToArray()`, or into your own list) before returning.

### Serializable payloads

`ISpanSerializable` covers everything the other forms cannot: strings, optional fields, variable structure. Implement three members — `MaxSize` (upper bound in bytes, used to size a stack buffer), `Serialize(ref SpanWriter)` and `Deserialize(ref SpanReader)`. `SpanWriter.Put` and `SpanReader.Get` cover the primitives, strings and `Guid`; write and read in the same order.

`MaxSize` must be a genuine upper bound for the payload you actually send — it sizes the buffer the writer gets, and an underestimate overflows it. Fixed-size fields contribute their `sizeof`; for strings use the static helpers `SpanWriter.GetMaxStringSize(str)` (and `GetMaxLargeStringSize` for strings written with the large-string overload), which account for the length prefix and the encoded byte count of that exact string.

### Size limits

A single RPC's payload is bounded by what fits in the packet stream — up to 64 KB per call, but a payload larger than the connection's packet size gets split across packets, delaying delivery of everything behind it. Treat RPCs as small messages: for anything large or continuous, a `SyncableField` such as [`SyncList<T>`](built-in-syncable-fields.md) synchronizes incrementally instead.

Megabyte-scale transfers — level files, downloadable content, replays — do not belong in the entity stream at all. Send them on your own channel through the [transport](../integration/transport.md), which lets you show real download progress, cancel, and throttle so the transfer does not crowd out gameplay traffic.

## Behavior details

# [Server](#tab/server)

Payload bytes are copied when `ExecuteRPC` is called, so the source value or span may be reused immediately afterwards. Serializable payloads are written into a stack buffer of `MaxSize` bytes at that moment.

# [Client](#tab/client)

Payloads are decoded in stream order with state. Value and serializable payloads are handed over as owned copies; span payloads point into the receive buffer and must be copied to outlive the handler.

# [Prediction / rollback](#tab/prediction-rollback)

Payload handling is identical during predicted execution (`ExecuteOnPrediction`) — the value goes straight to the handler locally. Re-simulation does not replay RPCs, so payload handlers run once per call.

***

> [!WARNING]
> **Common mistakes**
>
> * Storing the `ReadOnlySpan<T>` from a span RPC — it is only valid inside the handler; copy the data out.
> * `MaxSize` smaller than what `Serialize` writes — the stack buffer overflows; add up `sizeof` for fixed fields and `SpanWriter.GetMaxStringSize` for each string instead of guessing.
> * Serializing and deserializing in different orders — the payload decodes into garbage with no error.
> * Sending bulk data through RPCs — large payloads fragment across packets and stall the stream behind them; use a syncable collection for continuous data.

## Related pages

- [client-requests.md](client-requests.md)
- [syncablefield-basics.md](syncablefield-basics.md)
