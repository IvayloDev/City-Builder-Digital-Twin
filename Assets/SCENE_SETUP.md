# City Twin — Scene setup guide

How to set up **Game** and the **master Game Instance** prefab so you can run 1–4 instances with no statics.

---

## 1. Run the game

**Just load the Game scene.** Build order is set with **Game** first: open **File → Build Settings** and ensure **Game.unity** is at index 0. Then press Play from the Game scene (or build the player—it will start in Game). No Boot scene needed.

---

## 2. Boot scene (optional)

If you want a splash/loading screen before Game, use **Boot** (`Assets/Scenes/Boot.unity`) as the first scene and add **Bootstrap** to a GameObject: it loads the “Game” scene after ~0.1 s. Otherwise ignore Boot and run Game directly.

---

## 3. Game scene (`Assets/Scenes/Game.unity`)

This is where the 1–4 **game instances** live.

**Option A — Manual (copy/paste prefab)**

1. Drag **Prefabs/Game Instance** into the hierarchy **4 times** (or 1 for testing).
2. For each instance, select the root and set:
  - **Game Instance Root**:
    - **Instance Id**: 0, 1, 2, 3 (one per copy).
    - **Listen Port**: 9001, 9002, 9003, 9004 (one per copy).
3. Position/layout:
  - Either leave all at (0,0,0) and use **one shared camera** that sees the whole table,  
  - Or give each instance its own **Camera** and set **Viewport Rect** (e.g. Instance 0: X=0, W=0.25; Instance 1: X=0.25, W=0.25; …) so the screen is split into 4 quadrants.

**Option B — Bootstrap spawner (optional)**

- Add a small script in Game that runs once: instantiate the Game Instance prefab 4 times, set `InstanceId` 0–3 and `ListenPort` 9001–9004 on each, then position or assign viewports. The prefab already contains all per-instance logic.

---

## 4. Master prefab: “Game Instance” (`Assets/Prefabs/Game Instance.prefab`)

The prefab is the **master object**: duplicate it to get a new instance. Each copy must have a **unique** Instance Id and Listen Port.

**Already on prefab:**

- **Game Instance Root** (root GameObject): Instance Id = 0, Listen Port = 9001.
- **Main Camera** (child): one camera per instance.

**Add these to the root (same GameObject as Game Instance Root):**


| Component                                  | Notes                                                                                                                |
| ------------------------------------------ | -------------------------------------------------------------------------------------------------------------------- |
| **OSC Receiver** (extOSC)                  | Add Component → extOSC → OSC Receiver. Local Port is overridden at runtime by TileTrackingManager from Listen Port.  |
| **Tile Tracking Manager**                  | Uses Game Instance Root’s Instance Id and Listen Port; binds `/tuio/2Dobj`.                                          |
| **Simulation Engine**                      | Holds tiles, metrics, QOL. Assign building catalog and transit graph from config at runtime (coordinator does this). |
| **Game Config Loader**                     | Config Path = `game_config.json`. Loads on first access; each instance has its own.                                  |
| **Session Timer**                          | Intro → Gameplay → End; config comes from coordinator.                                                               |
| **Game Instance Coordinator**              | Wires OSC → simulation, budget, config, and starts session timer. Assign refs below.                                 |
| **Localization Service**                   | Uses config loader’s localization; set Current Language or leave default from config.                                |
| **Tooltip Service**                        | Intro/end messages; assign refs to status bar and end panel UI.                                                      |
| **Dashboard Controller**                   | Timer, budget, QOL, 5 metric bars; assign refs to UI texts and fill images.                                          |
| **Tile Tracking Debug Overlay** (optional) | Toggle with F1; shows packets/sec and last pose.                                                                     |
| **Hub Population Debug Overlay** (optional) | Toggle with F2; shows each hub’s HubId and Population (requires HubRegistry in scene).                              |


**Coordinator references (assign in Inspector):**

- Simulation Engine → the same object’s SimulationEngine.
- Tile Tracking Manager → the same object’s TileTrackingManager.
- Config Loader → the same object’s GameConfigLoader (or child).
- Session Timer → the same object’s SessionTimer.
- Building Spawner → the same object’s or child’s BuildingSpawner (spawns building markers when TUIO tiles are added; remove when removed).

**SimulationEngine:**

- The coordinator sets building catalog, config, and a **default TransitGraph** (4 hubs in a square with roads). Buildings must be placed **within 200 units of a road segment** to affect metrics. To use a custom map, replace `BuildDefaultTransitGraphIfNeeded()` in the coordinator with your own graph setup or load from config. For **prefab-driven residential hubs** (population from scene, not JSON), see §5.

**Dashboard / UI:**

- Add a **Canvas** (child of the prefab root or a child object) with:
  - **Timer** (Text): assign to Dashboard Controller → Timer Text.
  - **Budget** (Text): assign to Budget Text.
  - **QOL** (Text): assign to Qol Text.
  - **Access** (Text): assign to Access Text.
  - **5 metric bars**: 5 UI Images with **Image Type = Filled**, assign to Environment Fill, Economy Fill, Health Safety Fill, Culture Edu Fill, Accessibility Fill.
- **Tooltip Service**: assign Status Bar Text and, if you have an end panel, End Title Text, End Body Text, End Panel (GameObject).

**Building Spawner (TUIO → visuals):**

- Add **Building Spawner** (e.g. on the same root or on the Canvas). Assign **Content Root** to a RectTransform that defines the table area (e.g. a child of Canvas). Assign **Building Marker Prefab** to a prefab that will be instantiated per placed tile (can be a simple Panel with **Building Marker Display** + TextMeshPro to show building id/name). Set **Table Size** (e.g. 300, 300) so TUIO 0–1 maps to that range; enable **Flip Y** if your TUIO Y is top-down.

**Session Timer:**

- Coordinator starts it when the instance enables; intro/gameplay durations come from config. No need to start it manually.

**Per copy (when you duplicate the prefab in Game):**

- Set **Instance Id** and **Listen Port** so each instance is 0/9001, 1/9002, 2/9003, 3/9004.

---

## 5. Residential Hubs (Prefab-Driven)

Residential hubs are deterministic, curated objects placed in the scene. Their population comes from hub prefabs, not from JSON config.

### 5.1 Decision and rationale

We use **4–5 Hub prefabs** placed manually in the scene. Each prefab carries its own **HubId** (string), **Population** (int), and optional display metadata (label, icon, ring scale). No runtime random spawning; no population in `game_config.json`. This approach keeps the layout stable and repeatable for installation/demo; population is a core balancing lever tied to hub position; prefab ownership keeps hub visuals and data in sync.

### 5.2 Hub prefab(s) and ResidentialHubMono

Create a prefab: **Prefab_HubResidential**.

**Components:**

- **ResidentialHubMono** (script)
- Visual (SpriteRenderer / Mesh / UI element, depending on map rendering)
- Optional collider (only if overlap checks or debug selection are needed)

**ResidentialHubMono fields:**

- `string HubId` (unique, e.g. H1, H2, H3 …)
- `int Population` (e.g. 60000, 90000, 150000, 200000)
- `bool ShowDebugGizmos` (optional)
- (optional) `Transform VisualRoot` for scaling based on population

**Prefab variants (recommended)** so population is baked visually:

- Hub_60k, Hub_90k, Hub_120k, Hub_150k, Hub_200k

### 5.3 HubRegistry (scene scan and validation)

Create **HubRegistry** that runs on scene start and finds all hub instances. Add one **HubRegistry** component to the Game scene (e.g. on a root GameObject). Place 4–5 instances of **Prefab_HubResidential** in the scene and set each **HubId** and **Population** in the Inspector.

**Responsibilities:**

- Find all `ResidentialHubMono` in the scene
- Validate: 4–5 hubs exist; HubId is unique; Population > 0
- Expose read-only list to the simulation engine

**Interface:** `IReadOnlyList<ResidentialHubMono> Hubs`

**Failure mode:** If validation fails, log a clear error and block game start (fail fast).

### 5.4 Simulation use of hubs

When prefab-driven hubs are used, the simulation engine must **not** depend on config for hub positions or population. The coordinator/engine takes hubs from **HubRegistry**.

**Pipeline:**

1. HubRegistry provides hubs.
2. Simulation queries each hub for **Position** (transform) and **Population**.
3. When a tile changes: compute radial distance from tile to hub; apply metric formulas using that hub’s population.

**Formulas:**

- **Standard metrics (4 metrics):** `(BaseValue × Population) / RadialDistance` (RadialDistance clamped by epsilon).
- **Accessibility:** `(Importance × Population) / TransitDistance` (TransitDistance clamped by epsilon).

### 5.5 What stays in JSON

Hubs and populations are **not** in JSON. Everything else remains in `StreamingAssets/game_config.json`: session timing (intro + gameplay), budget, building definitions (12 types: cost, base values, importance, impact size), scoring constants (epsilon distance, QOL cap), accessibility constants (walking/snap thresholds), OSC sources, tooltips, localization.

### 5.6 Recommended population set

Use distinct values so strategy choices matter:

- **4-hub set:** 60k, 90k, 150k, 200k
- **5-hub set:** 60k, 90k, 120k, 150k, 200k

### 5.7 Acceptance criteria (Hub/Population)

- Scene contains 4–5 hub instances.
- Each hub has a unique HubId.
- Population is set on the prefab (Inspector) and is > 0.
- On play, HubRegistry logs the hub list and validates.
- Simulation uses hub.Population in all scoring formulas.
- Changing prefab population changes scoring without touching JSON.

### 5.8 Task list (Cursor execution)

1. Create **ResidentialHubMono** script and prefab(s).
2. Place 4–5 hubs in the Game scene.
3. Implement **HubRegistry** (scene scan + validation).
4. Wire simulation to use HubRegistry hubs (no JSON dependency for hubs).
5. Add debug overlay: show each hub’s population on screen (toggle).
6. Final test: change a hub prefab’s population and verify metric change.

---

## 6. Quick checklist

- (Optional) Boot: only if you want a splash screen; one GameObject with **Bootstrap**. Otherwise run **Game** as the first scene.
- Game: 1–4 copies of **Game Instance** prefab; Instance Id 0–3, Listen Port 9001–9004.
- Prefab root: **Game Instance Root**, **OSC Receiver**, **Tile Tracking Manager**, **Simulation Engine**, **Game Config Loader**, **Session Timer**, **Game Instance Coordinator**, **Localization Service**, **Tooltip Service**, **Dashboard Controller** (and optional debug overlay).
- Coordinator: all refs set to the components on the same prefab (or children). Optional: assign **Hub Registry** (scene-level) so simulation uses prefab-driven hub positions and population (see §5).
- Dashboard: Canvas with timer, budget, QOL, access texts and 5 fill images assigned.
- Tooltip Service: status bar and end panel refs assigned if you use them.
- StreamingAssets: `game_config.json` present (already added).
- **Hubs:** 4–5 hub instances in scene; HubRegistry validates on play (see §5).

No statics: each instance gets its own config load, simulation, OSC port, and UI. Copy/paste the prefab and set Id + Port to add another instance.

---

## 7. Next steps

1. **Prefab and UI** – Add the components listed in §4 to the Game Instance prefab and assign refs (coordinator, dashboard, tooltip). Add a Canvas with timer, budget, QOL, access texts and 5 fill images; wire them to Dashboard Controller and Tooltip Service.
2. **Run and test** – Open the Game scene, add one Game Instance from the prefab, press Play. Send TUIO/OSC to `/tuio/2Dobj` or simulate placements; confirm metrics update when buildings are placed **near the default roads** (within ~200 units of the square’s edges).
3. **Coordinate system** – Ensure your physical table / TUIO coordinates match the default graph (hubs at 60–240 on a 300 unit scale). If your coordinate space is different, adjust `BuildDefaultTransitGraphIfNeeded()` in GameInstanceCoordinator (e.g. scale or offset) or load graph from config.
4. **Custom map (optional)** – Replace the default graph with config-driven or procedural nodes/edges (e.g. like the HTML’s `generateMap()`), or expose graph setup in the Inspector for per-instance maps.
5. **Residential hubs (optional)** – When using prefab-driven hubs: complete the task list in §5.8 (ResidentialHubMono, HubRegistry, wire simulation, debug overlay, test).
6. **Polish** – Efficiency/connection feedback (e.g. show “Connected to road” vs “Not connected”), end-game screen with tooltips, localization keys in `game_config.json`.