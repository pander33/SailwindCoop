# Dev Console (development only)

A separate BepInEx plugin that runs C# inside the running game over a loopback HTTP port, so a
question about live state ("is this item a member of the house?", "what is in the host's item
cache?") can be answered without a rebuild.

**It executes arbitrary code in the game process. Never ship it and never copy it to another
player.** It builds into its own folder, `BepInEx/plugins/SailwindCoopDevConsole/`, so it is not part
of the `SailwindCoop` plugin folder that gets packed. Delete that folder to remove it.

## Build

```bash
dotnet build Tools/DevConsole/DevConsole.csproj -c Release
```

`Mono.CSharp.dll` (NuGet, the evaluator) is copied next to the plugin; the game does not ship it.

## Use

The port is 7778. If it is busy, the next free one up to 7787 is taken — so with two copies of the
game on one machine the first started gets 7778 and the second 7779. The chosen port is in
`BepInEx/LogOutput.log`.

```bash
curl http://127.0.0.1:7778/status
curl -H "X-DevConsole: 1" --data-binary "Dev.All<ShipItem>().Length" http://127.0.0.1:7778/run
curl -H "X-DevConsole: 1" --data-binary @script.cs http://127.0.0.1:7778/run
curl -H "X-DevConsole: 1" -X POST http://127.0.0.1:7778/reset
```

- `POST /run` — the body is C#. The response is JSON: `ok`, `type`, `value`, `error`, and `out`
  (lines written with `Dev.Print`).
- The session is persistent: variables and `using`s from one request stay for the next.
  `POST /reset` starts a clean one.
- Code runs on Unity's main thread, so it may touch any game object. A request waits up to
  `TimeoutSeconds` (15) for the main thread.

Every loaded assembly is referenced, including `SailwindCoop`, and these namespaces are imported:
`System`, `System.Linq`, `System.Collections.Generic`, `UnityEngine`, `SailwindCoop`,
`SailwindCoop.Net`, `SailwindCoop.Sync`, `SailwindCoop.Runtime`, `SailwindCoop.DevConsole`.

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
- `DevConsole.Enabled = false` in `BepInEx/config/com.sailwind.coop.devconsole.cfg` turns it off
  without removing the files.

Any local program can still use it: treat the game as running whatever was sent to that port.
