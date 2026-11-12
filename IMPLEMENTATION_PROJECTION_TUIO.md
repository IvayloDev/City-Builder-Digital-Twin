# Implementation plan: 16:10 projector frame, pre-calibration TUIO alignment, Next-driven camera calibration

Written 2026-11-12 for whoever picks this up (any model). Everything here is grounded in the code as of today; verify a line before relying on it.

## Ground rules (from the user, non-negotiable)

- **Never enter Play mode and never trigger a recompile while the user is in Play mode.** The user runs and verifies. You edit, the user presses Build And Run.
- **Never add player-facing text** that is not in the client translation matrix. Staff-only text (calibration menu, on-table calibration banners, the `TUIO live:` debug readout) is accepted.
- Unity has the base checkout open: `C:\Users\hadsh\Desktop\City-Builder-Digital-Twin-main`. Edit files there (the Write tool may refuse that path from a worktree; use Bash with python, keep LF endings, keep the `.meta` files).
- Unity 6000.3.8f1, Input System, TextMeshPro, extOSC. Unity MCP is usually disconnected; work from disk and the Player.log under `AppData\LocalLow\<company>\<product>\`.

## How it works today (read these first)

| Piece | File | What it does |
|---|---|---|
| Output rig | `Assets/Scripts/CityTwin/Calibration/ProjectionOutputRig.cs` | Renders the single Screen-Space-Camera canvas (ref 1920x3840) into a square 3840x3840 "table" RenderTexture. Table uv (0..1, y up) is the common coordinate space. `TryWorldToTableUv`, `TryTableUvToWorld`, `TryTableUvToProjector`, `MarkAllDirty`. |
| Projector | `ProjectorOutput.cs` + `Assets/Shaders/ProjectorWarp.shader` | Draws one `ProjectorCalibration` frame. `_camera.rect = ProjectionMath.FitAspect(viewport, displayPixels, Calibration.aspect)` letterboxes the frame to `aspect` inside the window. The frame is the quad `tablePoints` (table uv, order BL, BR, TR, TL) mapped to `projectorPoints` ((0,0),(1,0),(1,1),(0,1)). |
| Calibration data | `ProjectorCalibration.cs` | `aspect = 16f/9f` default. `DefaultSingleProjectorLayout()` builds the pilot placeholder: width 0.76 table uv, `height = width * 9/16`, centred on `PilotFrameCenter`. |
| Auto fit | `ProjectorAutoFit.cs` | 0.5 s after load, when no site/shipped calibration exists, frames the placeholder around the visible game UI, forcing `cal.aspect` (fallback `16f/9f`). |
| Window | `Assets/Scripts/CityTwin/Core/OutputMonitor.cs` | Moves the main window full-screen to the non-primary monitor (`-outputMonitor N` override, 0 = stay). Already resolution agnostic. |
| TUIO in | `Assets/Scripts/CityTwin/Input/TileTrackingManager.cs` | extOSC `/tuio/2Dobj`. Raw = (x, y) 0..1 as sent by reacTIVision (y down). `IsMapped` is true only when a **fitted** `SensorMapping` exists; then `ToFilterSpace` = `mapping.Map(raw)` -> table uv, and `FilterSpaceToPose` = `rig.TryTableUvToWorld` -> `spawner.LocalToTuioPosition`. When NOT mapped the raw 0..1 goes straight to `BuildingSpawner.TuioToLocalPosition` (which applies `flipY`), i.e. raw camera coords are treated as canvas coords. **That is why an uncalibrated pin lands far from the tile.** |
| Camera mapping | `SensorMapping.cs`, `TableCalibrationFile.cs` | `Map(raw)` = homography(undistort(raw)). A saved sensor `IsFitted` when `pointCount >= 4 && mapping != null`. |
| Camera calibration flow | `SensorCalibrationTab.cs`, `SensorCalibrationSession.cs`, `CalibrationMenu.cs` (`DrawCameraTab`, `DrawCaptureCard`), `ProjectorOverlay.cs` | `StartCapture()` builds 9 targets (`TargetCount`) spread over the play area; `Session.Feed()` each frame; a tile resting within `StableRawRadius` for `StableSeconds` (1 s) auto-captures **and advances** (`Capture()` -> `Advance()`); after the 4th capture it refits; `IsComplete` -> `FinishCapture()` saves. Keys: Space capture now, X skip, Backspace back, R restart, V verify. Banner on the table: `BannerText()` ("CAMERA: put a tile on the yellow cross (n/9)   hold still 37%"). |
| Menu | `ProjectionCalibrationController.cs` | Opens with **Ctrl+Shift+F4** or the runtime CALIBRATION button (`CalibrationButtonInstaller.cs`). Only works while `ProjectionOutputRig` is enabled (it is, in DemoScene). IMGUI, drawn on the main window. |
| Debug readout | `Assets/Scripts/CityTwin/Core/TuioInputReadout.cs` | Label under "DROP HERE TO REMOVE": `TUIO live:N msgs:N :3333 last x,y` (raw as received). Use it to check orientation. |

reacTIVision side (already done 2026-11-12, `C:\Users\hadsh\Desktop\reacTIVision-1.6-win64`): custom build persists the image flip/rotation in `reacTIVision.xml` as `<image ... flip="h" rotate="0"/>` (TAB = h, Backspace = v, F3 = rotate; saved on ESC exit). `camera.xml` = Innomaker id 5, mjpeg 1920x1080 @ 15 fps (the id is an enumeration index and can change; `reacTIVision.exe -l` lists them). TUIO goes to UDP 127.0.0.1:3333. The flip is applied to the frame **before** detection, so TUIO coordinates follow it. The user says the correct orientation is the horizontal flip.

## Task A: frame is 16:10, centred, on any output resolution

Goal: the projected game frame keeps a 16:10 shape, letterboxed and centred in whatever the output monitor is (1920x1080 today), never stretched.

1. `ProjectorCalibration.cs`: change `public float aspect = 16f / 9f;` to `16f / 10f`. In `DefaultSingleProjectorLayout()` change `height = width * 9f / 16f` to `width * 10f / 16f`. Update the doc comment ("16:9 window" -> "16:10").
2. `ProjectorAutoFit.cs`: fallback `16f / 9f` -> `16f / 10f` (line ~78). Keep using `cal.aspect` first.
3. Check `DefaultTwoProjectorLayout()` and any other `9f / 16f` / `16f / 9f` literal: `grep -rn "9f / 16f\|16f / 9f" Assets/Scripts`. Change only the ones that describe the projector frame shape. Today the hits are exactly the two above plus `SensorMapping.aspect` (the camera's pixel aspect, 1920x1080 = 16:9): leave that one.
4. `ProjectorOutput.cs` already letterboxes via `FitAspect`; `ProjectorWarp.shader` already returns black outside uv 0..1. Nothing else needed for centring.
5. Shipped/site calibration files override the default: check `Assets/StreamingAssets` for a table calibration json (`TableCalibrationStore` load order: persistentDataPath -> StreamingAssets -> built-in). Checked 2026-11-12: `Assets/StreamingAssets` has no calibration file (only game_config.json and road layouts), so the built-in default applies unless a site file exists in persistentDataPath on the dev PC; delete a stale one there if the log says the source is not BuiltInDefault.

Done looks like: Player.log has `[OutputMonitor] Main window on 'PT-VW545NE' 1920x1080` and `[ProjectorAutoFit] ... (aspect 1.60)`; a DPI-aware `PrintWindow` capture of the game window shows the frame centred with equal black bars left and right (1728x1080 image area on a 1920x1080 output). To capture: PowerShell, `SetProcessDpiAwarenessContext(-4)` then `PrintWindow(hwnd, hdc, 2)`; `CopyFromScreen` returns black for this DirectX window.

## Task B: TUIO aligned to the full 16:10 frame before any calibration

Goal: with no camera fit, assume the camera sees exactly the projected frame: raw (0,0) = top-left of the frame, raw (1,1) = bottom-right. Pins then land roughly under the tiles; the camera calibration (Task C) refines it.

1. In `TileTrackingManager.cs` add a default mapping used when `_sensor == null || !_sensor.IsFitted` but the rig is active (`ProjectionOutputRig.Active == _rig && _rig != null && _spawner?.ContentRoot != null`):
   - Build it in `ResolveCalibration()` (also called from `RefreshCalibration()` on `rig.SensorCalibrationChanged`). Rebuild lazily whenever `rig.Projectors[0].tablePoints` differ from the ones last used (compare in `ToFilterSpace`), so the auto-fit and the projector editor are picked up.
   - `var cal = _rig.Projectors[0]` (pilot: one projector). Raw corners in the same order as `cal.tablePoints` (BL, BR, TR, TL) are `(0,1), (1,1), (1,0), (0,0)` because raw y is down and table uv y is up. `Homography.TrySolve(rawCorners, cal.tablePoints, out h)`; `_defaultMapping = new SensorMapping { homography = h }` (k1 = 0, no lens term).
   - Change `IsMapped` to be true when either the fitted sensor or `_defaultMapping` exists; `ToFilterSpace` uses `_sensor.mapping` when fitted, else `_defaultMapping`. Keep `FilterSpaceToPose` as is.
   - Log once: `[TileTracking] No camera fit: using the projector frame as the default camera mapping`.
2. `SensorCalibrationTab.MeasureJitter` and the verify view use `saved.mapping` only; leave them, they are calibration-only.
3. Orientation check (needs one run by the user): put a tile at the **bottom-left** of the projected frame. The readout `last x,y` should read about `0.0x, 0.9x`. If x is mirrored, the reacTIVision flip is wrong (TAB toggles it, then ESC to save); if y is mirrored, add the Backspace flip there. Do not add flips in Unity: the one `flipY` in `BuildingSpawner` is for the no-rig path and is bypassed when mapped.
4. The parked-pin path (`GameInstanceCoordinator.ShowParkedPin(pose)`) already uses the pose from `TileTrackingManager`, so it benefits automatically.

Done looks like: before any calibration, a tile placed near the bottom of the frame shows its parked pin near the bottom of the projected image (within a few cm), language select by tile works, and the readout coordinates match the physical position.

## Task C: camera calibration driven by Next, with a "remove all tiles" step

Goal, in the user's words: *"message to remove all pins if they are already set on the table; the app gives me a point, I put a pin, it stores the input, I click Next; another point, I move the pin, Next ... until this part of the table is done."*

Changes, all staff-facing:

1. `SensorCalibrationSession.cs`
   - Add a phase before target 0: `ClearingTable`. `Feed()` in that phase: if `markers.Count == 0` for `StableSeconds` -> phase `Targets`. Expose `IsClearingTable`.
   - Capturing no longer advances. `Capture()` sets `_states[CurrentIndex] = Captured`, stores the raw, refits (from the 4th point on), and sets `AwaitingNext = true`. Keep the hold-still auto capture (it IS the "stores the input" step) and Space as manual capture.
   - Add `public bool Next()`: only when `AwaitingNext` (or the target is Skipped) -> `Advance()`, `AwaitingNext = false`; returns false otherwise. `IsComplete` stays `CurrentIndex >= Targets.Count`, reached only through `Next()` on the last target.
   - While `AwaitingNext`, `Feed()` must not re-capture the same target (ignore markers; the spent-session logic already stops the same tile filling the next target until it moves `RearmRawDistance`).
   - `Back()` clears `AwaitingNext`.
2. `SensorCalibrationTab.cs`
   - `NextTarget()` -> `_session.Next()`, status "Capture this target first" when it returns false.
   - `HandleKeys`: Enter and N -> `NextTarget()`.
   - `BannerText()` (projected on the table, staff text):
     - clearing: `CAMERA: remove all tiles from the table`
     - waiting: `CAMERA: put a tile on the yellow cross (n/9)   hold still 37%`
     - captured: `CAMERA: stored (n/9)  -  press Next`
     - after the last Next: existing "Fit done..." status.
   - `ProjectorOverlay` marker kinds: keep `TargetCaptured`; make the current target's marker change colour while `AwaitingNext` so it is obvious it is stored.
3. `CalibrationMenu.cs` `DrawCaptureCard()`: add buttons `Next (Enter)`, keep `Capture now (Space)`, `Skip (X)`, `Back (Backspace)`. Show the clearing state ("Waiting for an empty table").
4. Tests: find them with `grep -rln SensorCalibrationSession Assets` and update for the new phase and `Next()`; add: capture does not advance; `Next()` advances; `Next()` without capture returns false; clearing phase ends only after an empty table for 1 s.

Done looks like: open calibration (Ctrl+Shift+F4 or CALIBRATION) -> tab 2 Camera -> Capture: the table says remove all tiles; once empty, target 1 appears; placing a tile and holding still stores it and the table says press Next; Next shows target 2; after 9 (skips allowed) the fit is saved with Save; in Verify the pink square sits under a sliding tile.

## Order and verification

A -> B -> C. Each is one build for the user. After each build read Player.log for exceptions and the `[OutputMonitor]`, `[ProjectorAutoFit]`, `[TileTracking]` lines, and ask the user for the reacTIVision marker position vs the readout when orientation is in doubt.

## Known open issues (not part of A-C)

- reacTIVision crashed repeatedly (0xc0000409 / BEX64) on mjpeg 1280x960; the new 1920x1080 @ 15 fps setting is untested for stability. Logs: `Run-reacTIVision-logged.bat` writes `reacTIVision_<timestamp>.log`.
- reacTIVision was once seen issuing a new session id almost every frame for the same fiducial (duplicate markers once the table unlocks). Re-check after the camera settings change; otherwise dedupe in `TileTrackingManager` by fiducial id + position.
- Building markers are gated until a language is picked and the tutorial is passed/skipped (`TutorialSequenceController.TileEditsBlocked`); parked pins show regardless.
