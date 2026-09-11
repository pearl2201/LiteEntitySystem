---
description: How to add LiteEntitySystem to a .NET, Godot, MonoGame or Unity project - via NuGet or by copying sources - and hook up logging.
---

# Installation and project setup

LiteEntitySystem targets .NET Standard 2.1 and installs either as a NuGet package (works for almost every engine) or as a copy of the sources plus the Roslyn analyzer/source generator DLL.

Whichever path you take, install the analyzer with it. It does two things:

* it turns a bad `SyncVar<T>` usage into a compile-time error (`SyncVar<T>` fields must only be modified through `.Value` — assigning a whole new struct (`_health = new SyncVar<byte>()`) silently breaks synchronization);
* it runs a **source generator** that emits the field accessors used by the synchronization system.

Any class that declares `SyncVar<T>` or `SyncableField` fields — and any containing type — must be declared `partial`. The generator reports `LES0002` if the modifier is missing. SyncVar fields cannot be `readonly` (`LES0003`). See [source generation and field access](../integration/source-generation.md) for the full rules, the generated code, and how to supply accessors by hand.

## Option 1: NuGet package

For pure .NET servers, Godot, MonoGame and other NuGet-capable environments:

**shell**

```
dotnet add package LiteEntitySystem
```

The package brings everything with it: the dependencies (LiteNetLib 2.x, K4os.Compression.LZ4) and the Roslyn analyzer/source generator — no separate analyzer setup needed. In Unity, NuGet also works through a NuGet-for-Unity tooling package if you prefer it over copying sources.

## Option 2: copy the sources

Copy two things from the [repository](https://github.com/RevenantX/LiteEntitySystem):

* the `LiteEntitySystem/LiteEntitySystem` folder (includes an `.asmdef` for Unity);
* `AnalyzerBinary/LiteEntitySystemAnalyzer.dll`.

With this path the dependencies are yours to provide: LiteNetLib 2.x and K4os.Compression.LZ4 (plus `System.Runtime.CompilerServices.Unsafe` where the target framework doesn't ship it).

## Unity setup

The [example project](https://github.com/RevenantX/LiteEntitySystemUnityExample) is the reference layout for the sources path. Requirements: Unity 2021.2 or later; IL2CPP and WebGL are supported.

* `Assets/Plugins/LiteEntitySystem/` — the library sources with their `.asmdef`.
* `Assets/Plugins/LiteEntitySystemAnalyzer.dll` — imported with the asset label `RoslynAnalyzer`, which is what makes Unity run it as an analyzer *and* as a source generator.
* `Assets/Plugins/Dependencies/` — `K4os.Compression.LZ4.dll` and `System.Runtime.CompilerServices.Unsafe.dll`.
* LiteNetLib — as a UPM package `com.revenantx.litenetlib` from the OpenUPM scoped registry (see the example's `Packages/manifest.json`); the library's `.asmdef` references the `LiteNetLib` assembly by that name.

## Hook up logging

The library logs through a pluggable `ILogger` and stays silent until you assign one. Do this once at startup, before creating any manager — most setup mistakes (unregistered types, missing base calls) are reported here.

**UnityLogger.cs**

```csharp
using LiteEntitySystem;

public class UnityLogger : ILogger
{
    public void Log(string log) => UnityEngine.Debug.Log(log);
    public void LogWarning(string log) => UnityEngine.Debug.LogWarning(log);
    public void LogError(string log) => UnityEngine.Debug.LogError(log);
}
```

**Startup**

```csharp
LiteEntitySystem.Logger.LoggerImpl = new UnityLogger();
```

Outside Unity, implement the same three methods over `Console` or your engine's log.

> [!WARNING]
> **Common mistakes**
>
> * Skipping the analyzer — `x = new SyncVar<T>()` compiles without it and breaks synchronization with no error at runtime.
> * In Unity, dropping `LiteEntitySystemAnalyzer.dll` into the project without the `RoslynAnalyzer` label — Unity then treats it as a plain plugin and the analyzer never runs.
> * Not assigning `Logger.LoggerImpl` — the library swallows all warnings and errors, and real problems (type registration mismatches, missing `base.RegisterRPC`) go unnoticed.

## Related pages

- [registering-entity-types.md](registering-entity-types.md)

- [starting-a-server.md](starting-a-server.md)
