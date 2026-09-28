# ScrollWheelGuard

Stops the KSP camera zooming when the mouse is over an IMGUI window.

No integration required. Every `GUI.Window` and `GUILayout.Window` in the game is detected
automatically, so this works for your windows, stock ones, and other mods' — installing the DLL is
the whole setup. An explicit API is available for UI that is not a `GUI.Window`.


### Logging setting

The stock **Scroll Wheel Guard** settings page includes **Disable all logging except errors**. When enabled, informational and warning messages from the mod are suppressed; error messages are always written to the KSP log.

## Why this is harder than it looks

KSP reads the wheel through several unrelated paths, and the obvious interception point does not
exist:

- `UnityEngine.Input.GetAxis` and `GetAxisRaw` are **externs with no IL body**. Harmony cannot
  prefix them. Anything that hooks them directly silently does nothing.
- `Input.mouseScrollDelta`, by contrast, *is* ordinary managed code and can be prefixed.
- Flight and map cameras read `GameSettings.AXIS_MOUSEWHEEL`, an `AxisBinding`.
- The editor does **not**. It has its own zoom plumbing — separate `VAB_CAMERA_ZOOM_SENS` and
  `Editor_zoomScrollModifier` settings — and never touches `AXIS_MOUSEWHEEL`.
- uGUI turns a wheel movement into an `IScrollHandler` call from `UnityEngine.UI`, which is a
  different assembly entirely from `Assembly-CSharp`.
- `ControlTypes.CAMERACONTROLS` does not reliably stop wheel zoom on its own.

So there is no single chokepoint, and the mod attacks the problem from several sides at once.

## How it works

**Detection** — where the cursor is:

- `ScrollGuardAutoWindows` postfixes every `Rect`-returning `GUI.Window` / `GUILayout.Window` /
  `GUI.ModalWindow` overload and feeds the returned rect to the guard. These are managed methods
  with real IL bodies, unlike `Input.GetAxis`.
- `ScrollGuard.Register` / `PushRect` add rects explicitly for anything that is not a `GUI.Window`.
- `ScrollGuard.IsBlocking` evaluates lazily and caches per frame, so script execution order against
  the cameras does not matter.

**Enforcement** — four layers, because no single one covers every scene:

1. **Direct prefix on `Input.mouseScrollDelta`.** One patch, covers every reader in every assembly
   including uGUI's input module. The highest-value layer.
2. **Call-site rewriting.** Methods that read the wheel get their `Input.GetAxis(string)` call
   operand swapped for `GuardedGetAxis(string)` — identical signature, so it is a drop-in. The shim
   sees the axis name at runtime, so callers that compute the name instead of using a string literal
   are covered too.
3. **Prefixes on the axis binding.** Every parameterless `float`-returning method on
   `AXIS_MOUSEWHEEL`'s type and its sub-objects (`primary`, `secondary`), with an identity check so
   only the wheel binding is affected and every other axis passes through untouched.
4. **`ScrollGuardZoomFreeze`.** Watches every float member on every camera controller, and pins the
   zoom-looking ones while blocking. A fallback for read paths the scan cannot find, and the
   diagnostic that names the member being written when the zoom comes from somewhere unknown.

Plus a `ControlTypes.CAMERACONTROLS` input lock, raised in step with the guard state and always
released on scene change.

Your own IMGUI scroll views keep working throughout: they read `Event.current`, which comes from the
native event queue and none of the above touches it.

## Usage from another mod

Nothing is required. If you want to cover UI that is not a `GUI.Window` — a bare `GUI.Box` panel, a
dropdown drawn outside its parent — reference `ScrollWheelGuard.dll` with `Private=False` and:

```csharp
// once, e.g. in Start
guardHandle = ScrollGuard.Register(() => windowRect, () => visible);

// on teardown
ScrollGuard.Unregister(guardHandle);
```

Or per frame from `OnGUI`, with no handle to manage:

```csharp
ScrollGuard.PushRect(windowRect);
```

Rects are GUI space (top-left origin) — the same rect you pass to `GUILayout.Window`. The flip
against `Screen.height` is handled internally. `ScrollGuard.BlockNow()` forces a block for this frame
and the next, for cases a rect cannot express.

Optionally declare the dependency so KSP orders assembly loading:

```csharp
[assembly: KSPAssemblyDependency("ScrollWheelGuard", 1, 0)]
```

## Settings

Scroll Wheel Guard adds a stock KSP custom settings page named **Scroll Wheel Guard**.
The page currently contains:

- **Block scroll wheel when mouse is outside the KSP window** — enabled by default.
  When enabled, wheel input is suppressed whenever the OS pointer is outside the complete KSP
  window. On Windows this uses the native top-level KSP window bounds, including borders and
  title bar. The setting is persisted with the current game and applies immediately.

All static fields, settable at runtime. On `ScrollGuard`:

| Field | Default | Purpose |
| --- | --- | --- |
| `Enabled` | `true` | Master off switch, no unpatching required |
| `BlockOutsideGameWindow` | `true` | Block wheel reads while the cursor is outside the complete KSP window; Windows uses the native top-level window bounds. This is controlled by the stock **Scroll Wheel Guard** settings page when a game is loaded. |
| `AutoDetectWindows` | `true` | Treat every IMGUI window as a blocker with no registration |
| `MinAutoRectSize` | `8` | Ignore auto-detected rects smaller than this |
| `DebugLogging` | `true` | Log state transitions, heartbeats and watcher findings |
| `FreezeCameraZoom` | `true` | Pin zoom-looking camera members while blocking |
| `FreezeAllCameraFloats` | `false` | Pin *every* camera float — stops all camera motion while hovering |
| `UseInputLocks` | `true` | Raise the `CAMERACONTROLS` lock too |
| `LockMask` | `CAMERACONTROLS` | Add `ControlTypes.MAP` to also suppress map interaction |
| `BlockWhileHotControl` | `false` | Keep blocking while an IMGUI control owns the mouse |
| `Padding` | `0` | Grow every rect, e.g. to cover a resize grip on the border |

On `ScrollGuardPatcher` (read once at load, so change them before the patch pass):

| Field | Default | Purpose |
| --- | --- | --- |
| `DumpDiagnostics` | `true` | Log the API dump at startup |
| `ForceDeepScan` | `true` | Examine every method, not just types matching `TypeHints` |
| `ScanAllAssemblies` | `true` | Scan past `Assembly-CSharp` — needed to reach `UnityEngine.UI` |
| `TypeHints` | camera-ish names | Type filter used only when `ForceDeepScan` is off |

`DebugLogging` and `DumpDiagnostics` default to on while this is being proven across scenes. Turn
both off for a quiet log once it works for you.

## Reading the log

Startup:

```
[ScrollGuard] prefixed Input.mouseScrollDelta directly; this covers every reader in every assembly.
[ScrollGuard] deep scan: N method(s) examined, C candidate(s), P transpiled, T ms.
[ScrollGuard] covered N wheel read(s) in Type.Method (N swapped, N filtered)
[ScrollGuard] UNMATCHED: Type.Method touches the wheel but no read shape was recognised.
[ScrollGuard] ready: ...
```

Per scene:

```
[ScrollGuard] runner alive in EDITOR (autodetect=True, freeze=True)
[ScrollGuard] EDITOR heartbeat: 0 registered window(s), 3 rect(s) seen so far, ...
[ScrollGuard] watching 47 float member(s) in EDITOR across 2 camera type(s): VABCamera SPHCamera
[ScrollGuard] blocking = true in EDITOR
[ScrollGuard] blocking = false in EDITOR; fired 12 filter(s) while blocked
```

Diagnosis, in order:

1. **No `runner alive` line** — the component is not running at all.
2. **Heartbeat says `NOTHING TO BLOCK OVER`** — no window was registered and none auto-detected, so
   the guard has nothing to block over and the patches are irrelevant.
3. **No `blocking = true` when hovering** — the hit test is failing. Check the mouse position and
   screen size reported in the heartbeat.
4. **`fired 0 filter(s) while blocked`** — the hit test works but this scene's wheel read is not
   covered. Look for `CHANGED WHILE BLOCKED:` lines; the member that moves names the class doing the
   zooming. `FreezeAllCameraFloats = true` will stop it bluntly in the meantime.
5. **`is loaded N times`** — a duplicate DLL in GameData. Each copy gets its own statics, so
   registrations and patches land in different worlds and nothing blocks. Delete all but one.

The `=== API dump ===` block records what KSP's wheel API actually looks like on your install:
whether each `Input` member has an IL body, the runtime type of `AXIS_MOUSEWHEEL` with all its
methods and fields, the wheel-owned instances, the candidate axis names, and every `GameSettings`
static mentioning WHEEL or ZOOM.

## Solution layout

```
ScrollWheelGuard.sln
Directory.Build.props                     resolves KSPRoot / KSPManaged / KSPGameData / HarmonyPath
KSPRoot.props.sample                      copy to KSPRoot.props and edit (gitignored)
src/ScrollWheelGuard/
  ScrollWheelGuard.csproj                 net472, SDK-style, builds into GameData
  Properties/AssemblyInfo.cs
  ScrollGuard.cs                          public API, hit test, per-frame state
  ScrollGuardAutoWindows.cs               GUI.Window hooks; why no registration is needed
  ScrollGuardPatcher.cs                   Harmony bootstrap, IL scan, shims, diagnostics
  ScrollGuardRunner.cs                    input lock, transition logging, heartbeat
  ScrollGuardZoomFreeze.cs                camera float watcher and zoom pin
  Samples/ExampleWindow.cs                reference usage, excluded from compilation
GameData/ScrollWheelGuard/
  ScrollWheelGuard.version                KSP-AVC
  Plugins/                                build output lands here
```

## Building

1. Install the [HarmonyKSP](https://github.com/KSPModdingLibs/HarmonyKSP) release so
   `GameData/000_Harmony/0Harmony.dll` exists. Do not bundle your own copy.
2. Copy `KSPRoot.props.sample` to `KSPRoot.props` and set `KSPRoot` to your install.
3. Open `ScrollWheelGuard.sln` and build, or `dotnet build` / `msbuild` from the repo root.

Output goes to `GameData/ScrollWheelGuard/Plugins/ScrollWheelGuard.dll`. Copy the
`GameData/ScrollWheelGuard` folder into your install, or set `DeployToKSP` to `true` in
`KSPRoot.props` (or pass `/p:DeployToKSP=true`) to have the build copy it.

Wrong paths fail the build early with a readable message from the `ValidateKspPaths` target rather
than a wall of missing-type errors.

Non-Windows installs: override `KSPManaged` in `KSPRoot.props`. Linux uses the same
`KSP_x64_Data/Managed` layout; macOS needs `KSP.app/Contents/Resources/Data/Managed`.

## Caveats

- **Load time.** `ForceDeepScan` plus `ScanAllAssemblies` means every method in every non-BCL
  assembly is examined once at startup. The scan logs its elapsed time; narrow it with `TypeHints`
  and `ForceDeepScan = false` if it costs too much, accepting that scene-specific read paths may be
  missed.
- **Global reach.** The `mouseScrollDelta` prefix and the call-site swaps affect every mod in the
  install while a window is hovered. That is the intent, but it is a wide blast radius.
- **`FreezeAllCameraFloats` is blunt.** It stops all camera motion while the cursor is over a
  window, not just zoom. Use it as a stopgap, not a setting.
- **Inlining.** Patching happens at `KSPAddon.Startup.Instantly`, before the cameras have JITted,
  which is the window where Harmony patches on short methods are reliable.
- **Lock leaks.** If something goes wrong mid-development, the debug menu (`Alt+F12` → Input Locks)
  shows a stuck `ScrollWheelGuard` entry. `OnDestroy` and `OnApplicationQuit` both clear it.
