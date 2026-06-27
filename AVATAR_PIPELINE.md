# AVATAR_PIPELINE.md

Project guide for remote player models, AssetBundles, and avatar animations.

## Current Runtime Model

Sailwind does not expose a normal visible PC player body that can be reused for LAN co-op. The mod renders remote players with its own `GameObject` in `Sync/PlayerSync.cs`.

Runtime behavior:

- `PlayerState` sends root pose, velocity, and `HeadRot`.
- Remote root motion is applied through `NetTransform`.
- If `BepInEx/plugins/SailwindCoop/avatar.bundle` exists, `PlayerSync` tries to load a character prefab from it.
- If bundle loading fails, the mod falls back to a simple code-built avatar: body, head, look marker, and name.
- Remote avatar colliders are removed so the model cannot block Sailwind raycasts/interactions.
- Remote avatar layer is set recursively to a layer rendered by the active camera.

Do not use Sailwind's Oculus `RemotePlayer` / `OvrAvatar` path for PC LAN avatars.

## AssetBundle Requirements

Use AssetBundle for any real Unity model. Runtime `.prefab` / `.fbx` import is not supported by the game.

Bundle rules:

- Build with Unity 2019.1.x, ideally close to Sailwind's Unity 2019.1.10f1.
- Build target: `StandaloneWindows64`.
- Output file expected by the mod:

```text
D:\SteamLibrary\steamapps\common\Sailwind\BepInEx\plugins\SailwindCoop\avatar.bundle
```

- Bundle header must look like `UnityFS ... 2019.1.x`.
- Unity 2020/2021/2022/6000 bundles may fail with `AssetBundle.LoadFromFile == null`.
- Keep shaders simple: Built-in Standard or Unlit. Avoid custom render-pipeline shaders.

Current loader search order:

1. `Modular Fantasy Character`
2. `Modular Fantasy Character.prefab`
3. `Cowboy`
4. Any bundle asset path containing `modular fantasy character`
5. Any bundle asset path containing `cowboy`
6. First loadable `GameObject` asset

If a new prefab name is used, either rename it in Unity or update `PlayerSync.GetAvatarPrefab`.

## Unity Setup For A Character

For the character model:

1. Select the character FBX/model.
2. `Rig -> Animation Type`: `Humanoid`.
3. `Avatar Definition`: `Create From This Model`.
4. Apply and configure until Unity shows a valid humanoid avatar.
5. Put the final visible prefab into the bundle, not just the FBX.

For Mixamo or external animation clips:

- Use `FBX for Unity`.
- For animation-only FBX files, use `Rig -> Humanoid -> Create From This Model`.
- Do not use `Copy From Other Avatar` unless the source FBX has the exact same skeleton hierarchy.
- Enable `Loop Time` for idle/run loops.
- Disable `Loop Time` for one-shot turn/action clips unless the state needs looping.

## Animator Contract

The mod currently drives these optional float parameters:

```text
Speed
Turn
```

`PlayerSync` checks `Animator.parameters`; if a parameter is missing, it is skipped.

Recommended controller:

- Default state: idle or locomotion blend tree.
- `Speed = 0`: idle.
- `Speed = 3`: run.
- `Turn`: range roughly `-1..1`, where positive/negative can trigger turn animations.

If only idle and run are configured, make transitions:

- `Idle -> Run`: `Speed > 0.1`
- `Run -> Idle`: `Speed < 0.1`
- Disable `Has Exit Time` for these transitions.
- Transition duration around `0.1`.

The mod intentionally sends animation intent, not raw metres/sec:

- Stationary: `Speed = 0`
- Moving: `Speed = 3`

This avoids half-blends after walk clips are removed.

## Pose Overlay

After Animator updates, `AvatarPoseDriver` adds a small upper-body pitch based on `HeadRot`.

It searches these bones:

- `Spine`
- `Spine1`, fallback `Chest`
- `Neck`

If a rig uses different bone names, update the lookup in `TryCreateBundledAvatar`.

If look pitch is inverted, change the sign in `AvatarPoseDriver.LateUpdate`. Do not change network protocol for this.

## Network Rules

Only change `Protocol.Version` when the wire format changes.

Current avatar-related wire data:

- `PlayerState.Pos`
- `PlayerState.Rot`
- `PlayerState.HeadRot`
- `PlayerState.Vel`

Do not add animation clip names or state IDs until simple motion/pose data is insufficient. Prefer deriving animation from existing pose/velocity first.

On-boat movement must remain boat-local. Do not compute animation speed from world movement of the rendered avatar: the boat itself moves and would make standing players appear to run.

## Debugging Checklist

Check `BepInEx/LogOutput.log` first.

Expected successful bundle lines:

```text
[PlayerSync] avatar.bundle assets: ...
[PlayerSync] Загружен avatar.bundle prefab '...'
[PlayerSync] Animator params: Speed=True, Turn=True
[PlayerSync] Pose bones: spine=..., chest=..., neck=...
```

Common failures:

- `Не удалось загрузить avatar.bundle`: wrong Unity version, wrong platform, corrupt bundle, or unsupported compression.
- `В avatar.bundle не найден GameObject prefab`: the bundle contains no prefab/GameObject, or only dependencies.
- `Speed=False`: Animator parameter is not named exactly `Speed`.
- Remote is always idle while overlay shows `Speed -> 3.0`: Animator transitions/controller are wrong.
- Remote runs while standing: animation speed is being derived from world movement; use `PlayerState.Vel` boat-local speed.
- Model blocks clicks: colliders were not removed or a child was instantiated after `StripColliders`.

Overlay line `Анимация` shows nearest remote animation values:

```text
Speed current -> target, Turn current, moving yes/no
```

Use it before changing animator code.

## Build And Test Loop

1. Rebuild `avatar.bundle` in Unity 2019.1.x after prefab/controller changes.
2. Copy it to `BepInEx/plugins/SailwindCoop/avatar.bundle`.
3. Build the plugin:

```bash
dotnet build SailwindCoop.csproj -c Release
```

4. Launch two game copies directly.
5. Host with `F9`, join with `F10`, show overlay with `F8`.
6. Verify:
   - model loads instead of fallback primitives;
   - name faces camera;
   - idle/run follows actual player movement;
   - standing on a moving boat stays idle;
   - upper body follows look pitch;
   - clicks/interactions still work through the avatar.
