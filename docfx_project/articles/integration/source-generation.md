---
description: How LiteEntitySystem reaches your SyncVar fields - the source generator, the partial/'readonly' rules, what code it emits, and how to supply field accessors by hand when you need to.
---

# Source generation and field access

Every `SyncVar<T>` you declare is an ordinary **field** inside your entity or `SyncableField` class. The synchronization system, however, works from a class id and a field index — it never knows your concrete type at the point where it reads or writes a value. This page explains how that gap is bridged, what it requires from your code, and how to take over manually when needed.

> **Upgrading from an earlier version.** The package no longer ships `RefMagic.dll` and the repository no longer contains `ILPart/`. Apart from adding `partial` where it is now required, your code does not have to change: field declarations, flags, RPCs and the wire format are all identical.

## Why generated code is needed

The system has to take a *reference* to a `SyncVar<T>` field of an object it only knows as `InternalBaseClass`, then mutate that struct in place. Historically this was done with runtime reflection metadata plus an IL helper assembly:

* the field's byte offset was read out of the runtime's internal field descriptor (`Mono`/`CoreCLR` specific),
* an IL assembly (`RefMagic.dll`) converted the object reference to a raw pointer and did `ldobj`/`stobj` at that offset.

Both parts are Mono/CoreCLR implementation details. They do not exist under IL2CPP, and converting managed references to raw pointers is unverifiable code that the WebGL toolchain cannot use. That is why the offsets and the IL assembly are gone.

The replacement is a **Roslyn source generator** shipped inside `LiteEntitySystemAnalyzer.dll`. For every type that declares synchronization fields it emits a typed accessor, so the framework gets a `ref` to the exact field without any offsets, reflection or pointer arithmetic.

## In this section

* [What you must do](#what-you-must-do) — the `partial` rule and the field rules.
* [What gets generated](#what-gets-generated) — the emitted override, and the two accessor shapes.
* [How the framework uses it](#how-the-framework-uses-it) — one-time resolution per entity class.
* [Providing accessors by hand](#providing-accessors-by-hand) — the escape hatch for types the generator cannot see.
* [Troubleshooting](#troubleshooting)

## What you must do

**1. Make sure the generator runs.** It ships in the same DLL as the `SyncVar` analyzer. With the NuGet package there is nothing to do; when copying sources into Unity, `AnalyzerBinary/LiteEntitySystemAnalyzer.dll` must be imported with the `RoslynAnalyzer` asset label, exactly as described in [installation](../getting-started/installation.md). That single DLL is both the diagnostic analyzer and the source generator.

**2. Declare types with synchronization fields as `partial`.**

```csharp
public partial class Door : EntityLogic
{
    private SyncVar<bool> _isOpen;
    public readonly SyncList<int> Keys = new();
}
```

A type needs `partial` when it **declares** at least one `SyncVar<T>` or `SyncableField` member. Types that only inherit synchronization fields (a `Door : EntityLogic` that adds no fields of its own) do not. If a type that needs it is missing the modifier, the generator reports the compile error `LES0002`:

```
error LES0002: Type 'Door' declares synchronization fields and must be declared as 'partial'
              (including all containing types) so that LiteEntitySystem can generate field accessors for it
```

Containing types count too: a `partial` entity nested in a non-`partial` outer class is an error, because the generated declaration has to re-open the outer type as well.

**3. Do not mark `SyncVar<T>` fields `readonly`.** A readonly field cannot be handed out as a mutable `ref` in portable C#, so the generator reports `LES0003`:

```
error LES0003: SyncVar field 'Door._isOpen' cannot be readonly.
              Remove the 'readonly' modifier so that the synchronized value can be updated
```

`readonly` on a **`SyncableField`** member (such as `Keys` above) is not only allowed, it is required — that is a different rule, enforced by the framework at registration. The two are easy to confuse: the *holder* of a `SyncableField` must be `readonly`, the `SyncVar<T>` fields *inside* it must not be.

**4. Ship the generator to every assembly that declares synchronization fields.** Accessors are emitted into the assembly that declares the type, so a shared assembly holding base entities or custom `SyncableField` types needs the generator (or [hand-written accessors](#providing-accessors-by-hand)) just like the game assembly does.

## What gets generated

Given this:

```csharp
public enum DoorState : byte { Closed, Opening, Open }

public partial class Door : EntityLogic
{
    private SyncVar<DoorState> _state;
    public readonly SyncKeys Keys = new();
}

public partial class SyncKeys : SyncableField
{
    private SyncVar<int> _count;
}
```

the generator adds a partial declaration to each type:

**Door.SyncVarAccessors.g.cs** (generated)

```csharp
namespace MyGame
{
    public partial class Door
    {
        protected override void RegisterSyncVarAccessors(SyncVarAccessorMap map)
        {
            base.RegisterSyncVarAccessors(map);
            global::LiteEntitySystem.EntityManager.EnsureFieldTypeRegistered<DoorState>();
            map.Add(typeof(Door), "_state",
                new SyncVarRefGetter<DoorState>(__lesObj => ref ((Door)__lesObj)._state));
            map.Add(typeof(Door), "Keys",
                new ObjectFieldGetter<SyncableField>(__lesObj => (SyncableField)((Door)__lesObj).Keys));
        }
    }
}
```

**SyncKeys.SyncVarAccessors.g.cs** (generated)

```csharp
namespace MyGame
{
    public partial class SyncKeys
    {
        protected override void RegisterSyncVarAccessors(SyncVarAccessorMap map)
        {
            base.RegisterSyncVarAccessors(map);
            map.Add(typeof(SyncKeys), "_count",
                new SyncVarRefGetter<int>(__lesObj => ref ((SyncKeys)__lesObj)._count));
        }
    }
}
```

There are only two accessor shapes:

| Shape | Emitted for | Signature |
|---|---|---|
| `SyncVarRefGetter<T>` | `SyncVar<T>` fields | `ref SyncVar<T> (InternalBaseClass obj)` |
| `ObjectFieldGetter<TField>` | `SyncableField` (and other class) fields | `TField (InternalBaseClass obj)` |

Both are keyed by the **declaring type** plus the **field name**, and both cast the object once. A `SyncVar<T>` accessor returns a reference into the live object, so the caller writes `_value` and `_interpValue` in place — no copies, no boxing and no pointer arithmetic.

Enum-valued sync vars additionally call `EntityManager.EnsureFieldTypeRegistered<TEnum>()`. Enums are not registered as field types by default, and unlike integers they cannot be inferred from a table of primitives, so the generated code registers one (only if you have not registered your own for that enum).

## How the framework uses it

Accessors are resolved **once per entity class**, lazily, when the first entity of that class is constructed:

1. `EntityClassData.ResolveAccessors` calls `RegisterSyncVarAccessors` on the entity instance. Because each generated override calls `base.RegisterSyncVarAccessors(map)` first, the map ends up containing the fields of every type in the inheritance chain.
2. For each field in the class's field table, the accessor is looked up by its declaring type and name and cached in the field info. `SyncableField` members are resolved one level deeper: the entity accessor yields the `SyncableField` instance, and that instance's own map yields its inner `SyncVar<T>`.
3. From then on, state application, interpolation, rollback, lag compensation, history and diagnostics all mutate fields through the cached accessor.

Two consequences worth knowing:

* **The wire format is unchanged.** Field ordering, packet layout and buffer offsets are still computed by the same code; only *how a field is reached in memory* changed. Peers built from before and after this change produce the same bytes.
* **Per-access cost is a cached delegate call.** Resolution happens once per class, not per entity update.

## Providing accessors by hand

You can write the override yourself instead of marking the class `partial`. The generator detects a `RegisterSyncVarAccessors` member declared on the type and stays out of the way entirely — no `partial` required, no `LES0002`/`LES0003`.

```csharp
// Deliberately NOT partial, and not touched by the generator.
public class ManualDoor : EntityLogic
{
    private SyncVar<DoorState> _state;

    public void CollectAccessors(SyncVarAccessorMap map) => RegisterSyncVarAccessors(map);

    protected override void RegisterSyncVarAccessors(SyncVarAccessorMap map)
    {
        base.RegisterSyncVarAccessors(map);   // keep base type fields working

        EntityManager.EnsureFieldTypeRegistered<DoorState>();   // enums: do this yourself

        map.Add(typeof(ManualDoor), "_state",
            new SyncVarRefGetter<DoorState>(entity => ref ((ManualDoor)entity)._state));
    }
}
```

Rules for a hand-written override:

* Call `base.RegisterSyncVarAccessors(map)` first whenever a base type declares synchronization fields, otherwise those fields lose their accessors.
* Register **every** synchronization field declared by that type — accessors are not merged with generated ones for the same type. Missing one leaves that field without an accessor.
* Keys must use the **declaring type** (`typeof(ManualDoor)`, or a base type if you are compensating for a base assembly) and the exact field name.
* Register enum field types with `EntityManager.EnsureFieldTypeRegistered<TEnum>()` before the map is used; the framework resolves the processor right after the override returns.
* Use `ref ((T)obj).field` directly. If a field must be `readonly`, you need a `ref` out of an `in` reference (`Unsafe.AsRef`), which is why the generator refuses readonly sync vars.

This is the recommended route for:

* assemblies compiled without the generator (a plugin or third-party library shipping `SyncableField` types),
* a base class living in such an assembly — a derived type can register the base's fields itself by keying on the base type,
* AOT/source-set setups where you control the code but not the build pipeline.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `LES0002` on a class | Class (or a containing class) declares sync fields but is not `partial` | Add `partial`, or replace generation with a hand-written override |
| `LES0003` on a field | A `SyncVar<T>` field is `readonly` | Remove `readonly` (or hand-write the accessor) |
| `error CS0111` duplicate `RegisterSyncVarAccessors` | A hand-written override clashes with a generated one from a stale build | Rebuild; the generator skips types that declare the override themselves |
| Log: `Missing generated accessor for field '...'` at runtime | The assembly that declares the type was compiled without the generator | Add the analyzer DLL to that project/assembly, or hand-write the override |
| Log: `Unregistered enum field type: ...` | An enum sync var whose generated registration never ran (typically an assembly without the generator) | Enable the generator there, or call `EntityManager.EnsureFieldTypeRegistered<TEnum>()` from a hand-written override |
| `Unregistered field type: Vector3` | A custom struct field type that was never registered | Register it with `EntityManager.RegisterFieldType<T>()` — see [SyncVar](../sync/syncvar.md) |

## See also

* [Installation and project setup](../getting-started/installation.md) — where the analyzer/generator DLL goes.
* [SyncVar](../sync/syncvar.md) — the `.Value` contract and supported field types.
* [Writing a custom SyncableField](../sync/custom-syncablefield.md) — inner sync vars and their accessors.
