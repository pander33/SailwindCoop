# Dev Console (development only)

A separate BepInEx plugin that runs C# snippets inside the running game, so a question about live
state ("is this item a member of the house?", "what is in the host's item cache?") can be answered
without a rebuild. `devrun.sh` compiles a snippet and posts it to a loopback HTTP port; the plugin
loads it and runs it on Unity's main thread.

**It executes arbitrary code in the game process. Never ship it and never copy it to another
player.** It builds into its own folder, `BepInEx/plugins/SailwindCoopDevConsole/`, so it is not part
of the `SailwindCoop` plugin folder that gets packed. Delete that folder to remove it.

## Build

```bash
dotnet build Tools/DevConsole/DevConsole.csproj -c Release
```

## Use

```bash
Tools/DevConsole/devrun.sh 'CoopBehaviour.Instance.Net.Role'
Tools/DevConsole/devrun.sh -p 7779 'Dev.All<ShipItem>().Length'
Tools/DevConsole/devrun.sh -f snippet.cs
curl http://127.0.0.1:7778/status
curl -H "X-DevConsole: 1" -X POST http://127.0.0.1:7778/reset
```

The port is 7778. If it is busy, the next free one up to 7787 is taken — so with two copies of the
game on one machine the first started gets 7778 and the second 7779. The chosen port is in
`BepInEx/LogOutput.log`.

- One expression (no `;`) is returned as the value. Anything else is a method body: end it with
  `return x;` to return something.
- The response is JSON: `ok`, `type`, `value`, `error`, and `out` (lines written with `Dev.Print`).
  A compile error is printed by `devrun.sh` itself and nothing is sent.
- Every snippet is its own assembly, so local variables do not survive. Keep values between
  snippets in `Dev.Vars["name"]`; `POST /reset` clears it.
- Code runs on Unity's main thread, so it may touch any game object. A request waits up to
  `TimeoutSeconds` (15) for the main thread.

Snippets see every assembly in `Sailwind_Data/Managed`, `BepInEx.dll`, `SailwindCoop.dll` and
`LiteNetLib.dll`, with these namespaces imported: `System`, `System.Linq`,
`System.Collections.Generic`, `UnityEngine`, `SailwindCoop`, `SailwindCoop.Net`,
`SailwindCoop.Sync`, `SailwindCoop.Runtime`, `SailwindCoop.DevConsole`. Private members are reached
through the `Dev.Get`/`Dev.Call` helpers.

### Why the snippet is compiled outside the game

The game's `mscorlib` is the .NET Standard profile: `AssemblyBuilder`, `TypeBuilder` and
`ILGenerator` are stubs that throw `PlatformNotSupportedException`. An in-process compiler
(`Mono.CSharp.Evaluator` was tried) cannot emit code there. `devrun.sh` therefore runs the SDK's
`csc` against the game's own assemblies (`-nostdlib`) and posts the DLL; `POST /run` takes that
DLL as its body. This needs the .NET SDK and `curl` on the machine.

### Helpers (`Dev.*`)

| Call | What it returns |
|---|---|
| `Dev.One<T>()`, `Dev.All<T>()` | active objects of a type |
| `Dev.AllLoaded<T>()` | also inactive objects and assets |
| `Dev.Type("Name")` | a type by short or full name |
| `Dev.Get(obj, "field")`, `Dev.Set(obj, "field", value)` | a field or property, private or not; pass a `Type` for a static one |
| `Dev.Call(obj, "Method", args)` | calls a method, private or not |
| `Dev.Dump(obj)` | all instance fields, inherited private ones included |
| `Dev.Path(component)` | the transform path from the scene root |
| `Dev.Items()` | one row per saved `ShipItem`: ids, `parentObject`, parent, sold, held, collider |
| `Dev.Print(value)` | adds a line to `out` |
| `Dev.Vars` | a dictionary that survives between snippets |

Examples:

```csharp
CoopBehaviour.Instance.Net.Role
Dev.Items().Where(r => (int)r["parentObject"] > 0).ToList()
Dev.Get(Dev.One<BoatLocalItems>(), "cachedItems")
```

## Safety

- The listener binds `127.0.0.1` only; nothing on the network can reach it.
- The `Host` header must name the loopback address.
- A `POST` must carry the `X-DevConsole` header and no `Origin`, so a web page open in a browser on
  the same machine cannot send code to the game.
- `POST /run` accepts only a compiled assembly, at most 1 MB.
- `DevConsole.Enabled = false` in `BepInEx/config/com.sailwind.coop.devconsole.cfg` turns it off
  without removing the files.

Any local program can still use it: treat the game as running whatever was sent to that port.
