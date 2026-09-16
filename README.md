# CS-37 Unity Wiring Guide: Backend → Core Stage Simulation

Step-by-step Editor setup for everything built across the stage-transition, Virtual Assistant, and backend-API work: from the OpenAI token bootstrap through to a scenario actually running end to end. Follow the parts in order — each one depends on GameObjects/assets created in an earlier part.

This assumes every script listed below already exists in `Assets/Scripts` (see the file checklist in Part 0). If Unity reports a `CS0246` for any type name mentioned here, that script is missing from the project — re-request it rather than guessing at a substitute.

---

## Part 0: File checklist

Before wiring anything, confirm these all exist in `Assets/Scripts`:

**Stage/scenario core:** `PatientStageDefinition.cs`, `PatientStageSequence.cs`, `PatientScenarioController.cs`, `SofiaPersona.cs`, `VirtualAssistantPersona.cs`, `SubtitleController.cs`, `SessionManager.cs`

**Stage-transition presentation:** `CinematicController.cs`, `CinematicCatalog.cs`, `PositionMarkerRegistry.cs`, `InteractableActions.cs`, `ActionMenuController.cs`, `ActionMenuUI.cs`, `ActionVisualSwitcher.cs` (optional - only for a prop with pre-built alternate meshes to swap between, e.g. the dental chair's headrest), `ReticleController.cs` (optional - only if you want the on-screen reticle/crosshair to change appearance per hover type)

**Backend API client:** `BackendConfig.cs`, `JsonArrayUtil.cs`, `RemoteApiDtos.cs`, `RemoteScenarioApiLoader.cs`, `ToolsActionsCatalog.cs`, `RemoteToolsActionsLoader.cs`, `RemoteOpenAiTokenLoader.cs`

**Player/input:** `PlayerController.cs`, `PlayerInputAction.inputactions`

**Pre-scenario calibration UI (Part 14):** `WelcomeScreenController.cs`, `ScenarioSelectionController.cs` — optional as far as the core simulation is concerned (everything above works fine with `SessionManager.startingState` set to `ScenarioActive` and no calibration UI at all), but this is the real, intended entry point for a trainee-facing build.

**In-simulation quit confirmation (Part 15):** `QuitConfirmationController.cs` — optional; the simulation runs fine without it, this just adds an Escape-triggered "are you sure?" dialog mid-run.

**Incorrect-action notice (Part 16):** `IncorrectActionController.cs` — optional; shows a deterministic "wrong action" notice whenever a trainee-performed action is rejected by `PatientScenarioController`.

Legacy/optional, safe to ignore for this guide: `RemoteScenarioLoader.cs`, `RemoteCinematicCatalogLoader.cs`, `CsvUtil.cs`, `ScenarioTemplate.csv` (the Google Sheets path, superseded by the Flask backend), `TraineeActionTrigger.cs` (superseded by `InteractableActions.cs`/`ActionMenuController.cs` — see Part 8).

---

## Part 1: BackendConfig (one asset, shared everywhere)

1. In the Project window, right-click → **Create → CS-37 → Backend Config**. Name it `BackendConfig`.
2. Select it and set **Base Url** to wherever the backend actually is (e.g. `http://32.192.73.78:8080`, no trailing slash needed).
3. Keep this one asset — every loader below references this same asset, so moving the server again is a one-field change here, nothing in code.

**Known gotcha, plain http:// + a real build (not the Editor):** if `Base Url` uses `http://` rather than `https://`, an actual built Player (Standalone .exe, or the Quest APK) will throw `InvalidOperationException: Insecure connection not allowed` the moment any loader calls `SendWebRequest()` — this check does **not** trigger in Unity Editor Play mode, only in a built Player, which is why testing entirely in-Editor against `127.0.0.1` can look completely fine and then fail the first time the same URL is hit from a real build. Fix in **Project Settings → Player → (the platform tab you're building for) → Other Settings → Configuration → "Allow downloads over HTTP\*"** — set it to **Always Allowed** (do this for every platform tab you actually build: Windows/Mac/Linux Standalone and/or Android, separately). The alternative, more production-appropriate fix is serving the backend over `https://` once it's on a real host, which sidesteps this setting entirely. All three loaders (`RemoteScenarioApiLoader`, `RemoteOpenAiTokenLoader`, `RemoteToolsActionsLoader`) now catch this exception and fail gracefully (falling back / logging, per their existing pattern) instead of leaving the scenario stuck waiting forever — but the Player Settings change above is still the actual fix, not just the graceful-failure logging.

---

## Part 2: OpenAI token bootstrap (`RemoteOpenAiTokenLoader`)

This should live on a GameObject that's active from the very start of the scene — a simple empty `GameObject` named **Bootstrap** works well, and can also hold the other backend loaders from Part 7.

1. Create an empty GameObject, name it `Bootstrap`.
2. Add Component → `RemoteOpenAiTokenLoader`.
3. Drag the `BackendConfig` asset (Part 1) into **Backend Config**.
4. Drag your `Credential_Storage.asset` into **Credential Storage** — the *same* asset your MBB OpenAI provider already references, not a duplicate.
5. Leave **Provider Id** as `OpenAI` (matches the `providerId` value already in that asset).
6. Leave **On Token Received** empty unless you want to wire up a debug UI Text to show the fetched token for troubleshooting.
7. This component's `[DefaultExecutionOrder(-1000)]` already makes it run early — no extra Script Execution Order setup needed, but if you ever see the OLD key still being used, that's the first thing to double check (Project Settings → Script Execution Order).
8. **Auto Fetch**: leave **checked** here if you have no calibration UI (Part 14) in this scene — the token fetches/applies automatically at scene start exactly as before. **Uncheck it once Part 14 is built** — a trainee's "Use server's AI token" checkbox on the Scenario Selection screen is what decides this now, and `WelcomeScreenController` calls `BeginFetch()` itself only if that checkbox was left checked. Leaving Auto Fetch on AND having Part 14's UI also call `BeginFetch()` is harmless (the second call just logs a warning and does nothing, thanks to the double-call guard) but means the "leave it unchecked to keep my own token" option on the UI doesn't actually do what it says, since the fetch already happened at scene start regardless.

---

## Part 3: SubtitleController (UI)

1. In your Canvas, create a `TextMeshPro - Text (UI)` element anchored to the bottom of the screen. Optionally add a background panel `Image` behind it.
2. On a GameObject (the Canvas itself, or a dedicated child), Add Component → `SubtitleController`.
3. Drag the TMP text element into **Subtitle Text**.
4. Drag the background panel (if you made one) into **Subtitle Panel**, otherwise leave empty.
5. Leave **Hold Duration** at 4, **Show Speaker Label** checked, and the three label fields (`Trainee`, `Sofia`, `Virtual Assistant`) as their defaults unless you want different display names.
6. You'll drag this same `SubtitleController` into SofiaPersona, VirtualAssistantPersona, and nowhere else.

---

## Part 4: SofiaPersona (if not already fully wired)

On Sofia's GameObject (should already have `LlmAgent`, `SpeechToTextAgent`, `TextToSpeechAgent` from the MBB setup):

1. Add Component → `SofiaPersona` if not already present.
2. Wire **Llm Agent**, **Speech To Text Agent**, **Text To Speech Agent** to the matching Building Block components on this same GameObject.
3. **Interaction Camera**: leave empty to use `Camera.main`, or assign explicitly (matters more once there are multiple cameras, e.g. a cinematic camera).
4. **Hover Collider**: assign a Collider sized to Sofia's model (a Capsule Collider works well), or leave empty to use a Collider on this same GameObject.
5. **Subtitle Controller**: drag in the one from Part 3.
6. **Fixed Response Delay**: leave at 0.6.
7. Patient Name/Medical History/Current Vitals/Scenario Stage/Stage Guardrail here are just STARTING values — `PatientScenarioController` overwrites them every stage transition via `SetStage()`, so don't worry about keeping these perfectly in sync with stage 0's data.

---

## Part 5: VirtualAssistantPersona (the bot)

Create a separate GameObject for the bot character (a simple 3D model, a floating orb, a kiosk screen — whatever fits the scene) — it now needs its own full **LLM Agent / Speech To Text Agent / Text To Speech Agent** trio, completely separate instances from Sofia's (never drag Sofia's own Agents in here — sharing an Agent between two characters mixes their conversation history/state).

1. On the bot's GameObject, add Meta's **LLM Agent**, **Speech To Text Agent**, and **Text To Speech Agent** Building Blocks. For **LLM Agent**, pick a distinct voice/model config from Sofia's if you want the bot to sound clearly different (e.g. a flatter, more "assistant" TTS voice vs. Sofia's patient voice) — this is exactly the STT/LLM/TTS full-separation the team settled on, not a lighter/partial share.
2. Add Component → `VirtualAssistantPersona`.
3. Wire **Llm Agent** / **Speech To Text Agent** / **Text To Speech Agent** to the three Building Blocks you just added. **Llm Agent is optional** — if you leave it unassigned, the bot falls back to speaking each stage's `stageHint` text verbatim with no escalation or open-ended Q&A (the old behaviour), which is fine for early testing but not the intended final design.
4. **Interaction Camera**: same as Sofia's — leave empty for `Camera.main`.
5. **Hover Collider**: a Collider covering the bot's model, distinct from Sofia's.
6. **Subtitle Controller**: the same `SubtitleController` from Part 3 — both characters and the trainee all share one subtitle line.
7. **Response Delay**: leave at 0.6 (only used for the bot's fixed confirmation line and the no-LlmAgent fallback — the LLM hint path paces itself off the network call).

With an `Llm Agent` assigned, the bot answers open questions ("what should I do now?") and gives progressively more direct hints the more times the trainee asks on the same stage — its system prompt is rebuilt automatically from the stage's `stageHint` data each time (see `VirtualAssistantPersona.BuildHintSystemPrompt()`). Its readiness/calibration **confirmation** replies (when a stage has `advanceConfirmationHandledByBot` set) stay fully deterministic regardless — that path never touches the LLM, so it can't be talked into confirming early or confirming without actually matching the trigger phrase.

---

## Part 6: PositionMarkerRegistry (Sofia's named positions/poses)

1. Create an empty GameObject, name it `PositionMarkers` (can live under Bootstrap or wherever makes sense in your hierarchy).
2. Add Component → `PositionMarkerRegistry`.
3. For each place Sofia needs to be teleported to during a transition (standing at the door, seated, reclined unconscious, etc.):
   - Create an empty child GameObject positioned/rotated exactly where Sofia should end up (rotation matters — it sets her facing direction too).
   - In `PositionMarkerRegistry`'s **Markers** list, add an entry: **Id** = the exact string your stage data uses for `pos_name` (e.g. `chair_seated_unconscious`), **Transform** = the empty GameObject you just placed, **Animator Trigger** = the Animator trigger name to fire at the same time (e.g. `GoUnconscious`) — leave blank if this marker is a pure position with no animation change.
4. Repeat for every `pos_name` value your STAGE data actually uses. Until the backend's LEFT JOIN ships, a stage might arrive with a bare numeric `pos_id` instead of a name — add a marker keyed by that number as a string (e.g. `"1"`) as a temporary stand-in if needed.

---

## Part 7: CinematicCatalog + CinematicController (cutscene overlay)

**Catalog:**
1. On the same `Bootstrap` GameObject (or its own), Add Component → `CinematicCatalog`.
2. In **Entries**, add one row per cutscene: **Cinematic Id** = the exact `csc_file`/`csc_id` value your stage data uses, **Video Url** = either a direct-download/streamable link to the hosted video file (not a Google Drive "view" link — see the class comment) for the real/production setup, **or**, for quick local testing before anything is actually hosted anywhere, a path to a clip sitting under a folder literally named `Resources` in this project (e.g. drop your test file at `Assets/Resources/Movies/sit_on_chair.mp4` and put `Assets/Resources/Movies/sit_on_chair.mp4`, `Resources/Movies/sit_on_chair.mp4`, or just `Movies/sit_on_chair` in this field — all three are accepted and treated the same). `CinematicController` decides which mode to use per entry automatically based on whether the value starts with `http://`/`https://`/`file://`. Until the backend's LEFT JOIN ships, key entries by the bare numeric `csc_id` as a string if that's what's arriving.

**Don't do this:** typing a raw `Assets/...` path expecting `VideoPlayer` to treat it like a file path — it won't. `VideoPlayer`'s URL mode only understands `http://`/`https://`/`file://` or a path relative to `StreamingAssets`; a bare `Assets/Resources/Movies/sit_on_chair.mp4` gets silently resolved against `StreamingAssets` (finding nothing) rather than the actual Assets folder, which is exactly what produces `WindowsMediaFoundation received empty file ...` / `Cannot read file.` in the Console. The Resources-folder form above is the supported way to test with a local file before you have real hosting.

**Overlay UI (build once, in your main Canvas):**
1. Create an inactive child GameObject `CutsceneOverlay` under your Canvas, covering the full screen.
2. Inside it, add a `RawImage` sized/anchored to a horizontally-centered band (leaving black space above/below — that's your letterbox). Add plain black `Image` bars above and below if you want them as separate elements rather than just the Canvas background showing through.
3. Leave `CutsceneOverlay` **inactive** in the Editor — `CinematicController` activates/deactivates it at runtime.

**Controller:**
1. Create a GameObject `CinematicController` (or reuse Bootstrap), Add Component → `CinematicController` (this auto-adds a `VideoPlayer` component via `[RequireComponent]`).
2. **Catalog**: drag in the `CinematicCatalog` from step 1 above.
3. **Player Controller**: drag in the scene's `PlayerController` component (on the player rig).
4. **Sofia Persona**: drag in Sofia's `SofiaPersona` component.
5. **Overlay Root**: drag in the `CutsceneOverlay` GameObject.
6. **Video Image**: drag in the `RawImage` inside it.

---

## Part 8: InteractableActions + ActionMenuController (right-click action menu on physical props)

Replaces the old `TraineeActionTrigger` (one action, fired by whatever UI you wired up yourself) with the flow the team wants: hover a prop, right-click to see a list of what you can do with it, left-click to choose. **Desktop (mouse) only for now** — VR/Quest support (a controller ray + button instead of the mouse) is a deliberate follow-up, not built yet; see the note at the top of `ActionMenuController.cs`.

**8a. Build the menu UI once, in your main Canvas:**

1. Create a child GameObject `ActionMenuBackdrop` under your Canvas — a full-screen `Image` (make it transparent: alpha 0 is fine, it just needs to cover the screen and block raycasts) with a `Button` component. Leave its `onClick` empty in the Inspector — `ActionMenuUI` wires that up in code. Leave it **inactive**.
2. Create a second child GameObject `ActionMenuPanel`, placed **after** `ActionMenuBackdrop` in the Hierarchy (so it renders on top and intercepts clicks before they reach the backdrop). Give it:
   - A `Vertical Layout Group` (so buttons stack top-to-bottom).
   - A `Content Size Fitter` set to **Height: Preferred Size** (so the panel shrinks/grows to fit however many actions a given object offers).
   - Its `RectTransform` **pivot set to (0, 1)** (top-left) — the panel grows down-and-right from wherever you right-clicked. **Anchor preset can be anything non-stretched** (centre, a corner, whatever the Rect Transform inspector defaults to) — `ActionMenuUI` works out the actual anchor point and positions relative to it correctly either way, so you don't need to match a specific anchor preset. (An earlier version of this script *did* assume a centred anchor and broke — the menu rendered pinned to the screen's top-left corner — if you picked a top-left anchor preset alongside the top-left pivot above; that's fixed now, not something to work around.)
   - Leave it **inactive**.
3. Inside `ActionMenuPanel`, create one `ActionButtonTemplate` — a `Button` with a TMP `Text` child for the label. Style it however fits your UI. Leave it **inactive** — this is cloned once per action at runtime, never shown directly.
4. Also inside `ActionMenuPanel`, create one more `ActionButtonCancel` — a normal `Button` with a TMP `Text` child reading "Cancel" (or similar). Unlike `ActionButtonTemplate`, this one is **real and permanent, not a clone template** — leave it **active**, and leave its `onClick` empty (`ActionMenuUI` wires it in code). It doesn't matter where you place it relative to `ActionButtonTemplate` in the Hierarchy — `ActionMenuUI` always moves it to the end of the list at runtime, so it renders after every real action regardless.

**8b. Add `ActionMenuUI`:**

1. Add Component → `ActionMenuUI` on the Canvas itself (or `ActionMenuPanel`'s parent — anywhere convenient).
2. **Canvas Rect**: drag in the Canvas's own `RectTransform`.
3. **Canvas Camera**: only needed if your Canvas's Render Mode is *Screen Space - Camera* — leave empty for the simpler *Screen Space - Overlay*. (If you're not sure which your Canvas uses, check the Canvas component's Render Mode directly — `ActionMenuUI` now reads that itself and ignores a wrongly-filled-in camera here, but an empty field is still the tidiest choice for Overlay.)
4. **Panel Root**: drag in `ActionMenuPanel`.
5. **Button Template**: drag in `ActionButtonTemplate`.
6. **Cancel Button**: drag in `ActionButtonCancel`'s `Button` component. Optional, but recommended — without it, trainees can only dismiss the menu by clicking away or pressing Escape, with no explicit option in the list itself.
7. **Backdrop Button**: drag in `ActionMenuBackdrop`'s `Button` component. Also optional, and complementary to (not made redundant by) Cancel — Backdrop catches trainees who just click elsewhere without reading the menu, Cancel is the explicit, discoverable option in the list. Recommended to keep both unless you specifically want to force an explicit choice (Cancel or Escape only).
8. **Screen Edge Margin**: leave at 8.

**8c. Add `ActionMenuController`:**

1. Create a GameObject `ActionMenuController` (or reuse `Bootstrap`), Add Component → `ActionMenuController`.
2. **Interaction Camera**: leave empty for `Camera.main`, same as Sofia's/the bot's.
3. **Menu UI**: drag in the `ActionMenuUI` from 8b.
4. **Player Controller**: drag in the scene's `PlayerController` — this is what gets disabled (and the cursor unlocked) while the menu is open, then restored the instant it closes, mirroring exactly what `CinematicController` already does during a cutscene.
5. **Crosshair**: optional — drag in your scene's on-screen aiming crosshair/reticle GameObject if you have one, and it'll be hidden while the menu is open (no reason to have a reticle sitting in the middle of a menu you're clicking through) and shown again the instant it closes. Leave empty if your scene doesn't use one.

**8d. Wire each interactable prop** (a syringe, a chair-recline lever, etc.):

1. Add Component → `InteractableActions` on the prop.
2. **Actions**: add one entry per thing the trainee can do with this object — **Action Id** = the exact string your stage data's `act_id` uses (e.g. `recline_chair`), **Label** = what shows in the menu (e.g. "Recline it"). For the dental chair example: two entries, `recline_chair`/"Recline it" and `set_upright`/"Set it upright".
3. **Hover Collider**: leave empty to use a Collider already on this GameObject, or assign one explicitly.
4. **Patient Scenario Controller**: leave empty — uses `PatientScenarioController.Instance` automatically.
5. **Tools Actions Catalog**: optional, drag in the one from Part 9 once it exists, purely so a typo in an Action Id gets logged as a warning at startup.

**8e. Optional: swap between pre-built alternate meshes when an action fires (e.g. the dental chair's headrest)**

If a prop has more than one pre-built mesh for different physical positions (a reclined headrest mesh and an upright headrest mesh, prepared as two separate GameObjects rather than one animated rig) and you want the matching one shown whenever the trainee picks the corresponding action from the menu:

1. Add Component → `ActionVisualSwitcher` on the same GameObject as the prop's `InteractableActions` (e.g. the dental chair).
2. **Interactable Actions**: leave empty to use the one on this same GameObject.
3. **Visuals**: add one entry per mesh - **Action Id** = the exact same string as one of `InteractableActions`' own **Action Id** entries (e.g. `recline_chair`), **Visual** = the GameObject for that mesh (e.g. the reclined headrest model). Add a second entry for `set_upright`/the upright headrest model. Not limited to two, if a prop ever needs more positions.
4. **Starting Visual Index**: the index (0-based, matching the order you listed them in step 3) that should be showing before the trainee has touched anything - e.g. `1` if upright is listed second and the chair should start upright. Leave at `-1` to leave whatever's active/inactive on each mesh GameObject exactly as authored in the Editor.
5. No other wiring needed - `InteractableActions.TriggerAction()` already raises the event this listens to (`OnActionTriggered`) the instant a valid action of this object's own is chosen, independently of whether that same action also happens to be the one that advances the current stage. The trainee can toggle back and forth between recline/upright as many times as they like; only exactly one of the listed meshes is ever active at once.

**8f. Optional: swap the on-screen reticle per hover type (talkable character vs. interactable prop)**

If you want the aiming reticle/crosshair itself to change appearance depending on what's under it - one look while hovering Sofia/the bot, a different one while hovering a syringe/chair/etc. - rather than a single unchanging crosshair:

1. Build (or reuse) a small UI `Image` centered on-screen for the reticle, if you don't already have one from Part 8c's **Crosshair** field.
2. On that same GameObject (or a nearby one), Add Component → `ReticleController`.
3. **Reticle Image**: drag in that `Image` component.
4. **Default Reticle** / **Talkable Reticle** / **Interactable Reticle**: for each, either fill in **Static Sprite** for a plain unchanging look, or leave Static Sprite empty and fill in **Animation Frames** with a sequence of Sprites to cycle through (at **Frames Per Second**) for a simple looping animation. There's no built-in way to drop in a raw `.gif` file directly - export/slice it into individual frame images (or a sprite sheet you slice in the Sprite Editor) first, the same way any other Sprite gets into a Unity project.
5. **Sofia Persona** / **Virtual Assistant Persona**: drag in each character, if present in the scene. Leave either empty if that character doesn't exist yet - talkable-hover detection just skips it.
6. No field for interactable-prop hover - it reads `ActionMenuController.Instance` automatically, same as every other script in this project that needs "the" action menu controller.
7. Go back to `ActionMenuController` (Part 8c) and point its own **Crosshair** field at this reticle's root GameObject (or a parent of it) if you haven't already - that's what hides/shows the whole reticle around the action menu being open; `ReticleController` itself doesn't need to know anything about the menu, since Unity simply stops calling its `LateUpdate()` while its GameObject is inactive.

This doesn't add any new hover detection - it polls the hover state `SofiaPersona`, `VirtualAssistantPersona`, and `ActionMenuController` are already computing every frame for their own purposes (hover-to-talk, the right-click menu), rather than raycasting a second time.

**If the menu shows fewer options than you configured:** check the Console first — `ActionMenuUI` now logs exactly how many action entries it received vs. how many buttons it actually spawned every time the menu opens, and warns specifically about any entry with a blank **Action Id** (the most common cause — an entry added to the `Actions` list in the Inspector with only the Label filled in, or added and never filled in at all). If the log says the right number were *spawned* but you still only *see* one, that's a `Vertical Layout Group` / button-height layout issue in `ActionMenuPanel` instead (check Spacing and that `ActionButtonTemplate`'s own height isn't 0).

No event wiring needed on the prop itself beyond this — `ActionMenuController` handles hover/right-click/menu display/left-click dispatch entirely on its own; you're just telling each prop what it offers.

---

## Part 9: ToolsActionsCatalog + RemoteToolsActionsLoader (optional validation)

1. On `Bootstrap`, Add Component → `ToolsActionsCatalog` (no Inspector fields to set).
2. Add Component → `RemoteToolsActionsLoader`.
3. **Backend Config**: drag in the Part 1 asset.
4. **Catalog**: drag in the `ToolsActionsCatalog` from step 1.
5. Go back to each `InteractableActions` from Part 8 and drag this catalog into their **Tools Actions Catalog** field, now that it exists.

---

## Part 10: RemoteScenarioApiLoader (fetches the actual stage content)

1. On `Bootstrap`, Add Component → `RemoteScenarioApiLoader`.
2. **Backend Config**: drag in the Part 1 asset.
3. **Scenario Id**: the `scn_id` to load (matches `SCENARIO.scn_id` — `1` for the seed data). Ignored once Part 14's UI is driving this (it calls `SetScenarioId()` itself right before `BeginLoading()`), but harmless to leave as-is.
4. **Timeout Seconds**: leave at 10.
5. **Patient Scenario Controller**: drag in the `PatientScenarioController` you'll create in Part 11 (you can come back and set this after Part 11 if it's easier).
6. **Auto Start Loading**: leave **checked** if you have no calibration UI (Part 14) in this scene — stages for **Scenario Id** above load automatically at scene start, same as always. **Uncheck it once Part 14 is built** — the trainee's own dropdown selection decides which scenario to load, and `WelcomeScreenController` calls `SetScenarioId()` then `BeginLoading()` itself, in that order, only once the trainee commits. Leaving this checked as well as wiring up Part 14 means the scene loads **Scenario Id**'s stages immediately at start AND (later) whatever the trainee picks — the first load isn't harmful, but it's wasted work and a confusing thing to see in the Console while testing.

---

## Part 11: PatientScenarioController (the hub — wire everything above together)

1. Create a GameObject `PatientScenarioController` (or reuse `Bootstrap`), Add Component → `PatientScenarioController`.
2. **Sofia Persona**: drag in Sofia's `SofiaPersona`.
3. **Virtual Assistant Persona**: drag in the bot's `VirtualAssistantPersona` from Part 5.
4. **Stage Sequence**: leave empty — `RemoteScenarioApiLoader` populates this at runtime. (Only assign a hand-authored `PatientStageSequence` asset here if you want a local fallback for when the backend is unreachable.)
5. **Cinematic Controller**: drag in the one from Part 7.
6. **Sofia Animator**: drag in the `Animator` component on Sofia's model.
7. **Sofia Transform**: drag in Sofia's root Transform (or leave empty to default to `sofiaPersona`'s own transform).
8. **Position Marker Registry**: drag in the one from Part 6.
9. **Allow Debug Advance Key**: leave checked during development (press `N` in Play mode to force-advance a stage without needing to talk to anyone) — turn off for a real trainee-facing build.
10. Go back to `RemoteScenarioApiLoader` (Part 10) and confirm its **Patient Scenario Controller** field now points at this component.

---

## Part 12: SessionManager (starts the whole flow)

1. This should already exist as a persistent GameObject (`DontDestroyOnLoad`).
2. **Starting State**: set to `ScenarioActive` while testing the patient scenario in isolation with no calibration UI in the scene at all. **Set to `ScenarioSelection` once Part 14 (Welcome Screen + Scenario Selection) is built** — `WelcomeScreenController` shows its own UI immediately regardless of this setting, but leaving `Starting State` at `ScenarioActive` alongside Part 14's UI would try to start the (wrong, default) scenario immediately AND show the calibration UI on top of it at the same time, which is confusing and pointless — `ScenarioSelection` is really just "don't auto-start anything, wait for a UI/script to call `BeginScenario()`/`SkipToScenarioActive()` explicitly," which is exactly what Part 14 does.
3. **Patient Scenario Controller**: drag in the one from Part 11.

---

## Part 13: Player rig (if not already set up)

Covered in an earlier pass of this project — briefly, for completeness of the chain:

1. `PlayerController` sits on the CharacterController-driven player root, with **Move Action**/**Look Action** pointing at the shared `PlayerInputAction.inputactions` asset's `Move`/`Look` actions.
2. **View Transform**: the desktop free-look camera (or the VR headset camera), parented under the player root so it inherits position when the CharacterController moves.
3. **Enable Mouse Look**: on for the desktop build variant, off for the VR build variant.
4. This is the same `PlayerController` referenced by `CinematicController` (Part 7) and read by `SofiaPersona`/`VirtualAssistantPersona`'s hover raycasts (via `Camera.main` or their explicit **Interaction Camera** field, which should point at this rig's camera).

---

## Part 14: WelcomeScreenController + ScenarioSelectionController (the real entry point)

Two screens, matching the placeholder UI already built: **Welcome Screen** (title, Start/Exit) → **Scenario Selection** (dropdown, read-only description, "Use server's AI token" checkbox, Cancel/Start). Assumes your dropdown/description field are TextMeshPro's `TMP_Dropdown`/`TMP_InputField` — the same TMP-first convention as `SubtitleController`/the action menu — swap the two field types in `ScenarioSelectionController.cs` if your placeholders actually use Unity's legacy `Dropdown`/`InputField` instead.

**14a. Add `ScenarioSelectionController` to your Scenario Selection panel:**

1. Add Component → `ScenarioSelectionController` on the Scenario Selection panel's root (or a child of it).
2. **Backend Config**: drag in the Part 1 asset.
3. **Timeout Seconds**: leave at 10.
4. **Scenario Dropdown**: drag in your `TMP_Dropdown`.
5. **Description Field**: drag in your `TMP_InputField` — this component locks it read-only (`interactable`/`readOnly` both set false/true) in `Awake()`, so you don't need to configure that yourself in the Inspector. **Separately, check the field's own Line Type is set to `Multi Line Newline`** (Inspector → the `TMP_InputField` component itself, not its child Text object) — TMP_InputField defaults to `Single Line` in some creation flows, and a Single Line field never wraps text no matter what else is configured; it just clips at the right edge exactly like "cut off by the field's border." Multi Line Newline is what actually lets long text like a scenario description wrap onto further lines within the box. If the description can run long, also make the field tall enough (or add a Scroll Rect) so the wrapped lines aren't themselves clipped at the bottom.
6. **Use Server Token Toggle**: drag in the "Use server's AI token" `Toggle`.
7. **Cancel Button** / **Start Button**: drag in the panel's own Cancel/Start buttons — **not** the Welcome Screen's Start button, a different one.
8. **Virtual Assistant Persona**: drag in the bot's `VirtualAssistantPersona` (Part 5) if you want the description read aloud on every selection change. Leave empty to disable the readback — the description field still updates either way.

**14b. Add `WelcomeScreenController` to your Welcome Screen (or a dedicated empty GameObject):**

1. Add Component → `WelcomeScreenController`.
2. **Welcome Panel**: drag in the Welcome Screen's root GameObject — this needs to be the object that contains **everything** visible on that screen (background art included), not just the buttons. If your background image/blur/title text live directly on the Canvas as siblings of a smaller "buttons only" panel, either re-parent them all under one object and assign that, or use **Calibration Root** below instead.
3. **Scenario Selection Panel**: drag in the Scenario Selection panel's root GameObject — same nesting requirement as step 2.
3a. **Calibration Root** (optional): if your Canvas has anything shared between the two screens that lives OUTSIDE both panels — a full-screen background image or blur that's always there regardless of which panel is showing — drag its root GameObject in here. It gets deactivated too, on top of the two panels, the instant the trainee clicks Scenario Selection's Start. **This is the fix if you see the Welcome Screen's background/title still visibly overlaid on top of the running simulation after Start** — that symptom means something is sitting outside the two toggled panels and was never being hidden by toggling them alone.
4. **Welcome Start Button**: drag in the Welcome Screen's own Start button (opens Scenario Selection — not the same button as 14a's **Start Button**, which actually launches the simulation).
5. **Exit Button**: drag in the Welcome Screen's Exit button.
6. **Scenario Selection Controller**: drag in the component from 14a.
7. **Player Controller**: drag in the scene's `PlayerController` — disabled for the entire Welcome/Scenario Selection flow, re-enabled the instant the trainee commits, mirroring `ActionMenuController`'s identical field.
8. **Crosshair**: optional, same idea as `ActionMenuController`'s own Crosshair field — hidden for the same duration.
9. **Remote Scenario Api Loader**: drag in the one from Part 10. Make sure Part 10's **Auto Start Loading** is unchecked (see that Part's step 6) — otherwise the scene also loads whatever **Scenario Id** is sitting in the Inspector immediately at start, on top of whatever the trainee later picks.
10. **Remote Open Ai Token Loader**: drag in the one from Part 2, if the "Use server's AI token" checkbox should actually do anything. Make sure Part 2's **Auto Fetch** is unchecked (see that Part's step 8) — otherwise the token already got applied at scene start regardless of what the trainee checks here.

**14c. Go back to Part 12 and set `SessionManager.Starting State` to `ScenarioSelection`** (see that Part's updated step 2) — `WelcomeScreenController` doesn't read this value at all (it shows its UI in its own `Awake()`/`Start()` regardless), but leaving `Starting State` at `ScenarioActive` would race the (wrong, default) scenario into starting immediately alongside the calibration UI appearing on top of it.

**What each button actually does, end to end:**
- Welcome Screen **Start** → hides Welcome, shows Scenario Selection, triggers a fresh `GET /api/scenarios` fetch to populate the dropdown (every time this panel opens, not just once).
- Selecting a different dropdown entry → updates the description field and (if wired) has the bot read the new description aloud.
- Scenario Selection **Cancel** → hides Scenario Selection, shows Welcome Screen again. World interaction stays locked throughout — cancelling doesn't let the trainee wander off, it just goes back a screen.
- Scenario Selection **Start** → hides both panels, unlocks player movement/rotation, optionally calls `RemoteOpenAiTokenLoader.BeginFetch()` (only if the checkbox was checked), calls `RemoteScenarioApiLoader.SetScenarioId()`/`BeginLoading()` for the chosen `scn_id`, and calls `SessionManager.Instance.SkipToScenarioActive()` — which is what actually calls `PatientScenarioController.BeginScenario()` and starts the first stage.
- Welcome Screen **Exit** → quits the application (or stops Play mode, in the Editor).

---

## Part 15: QuitConfirmationController (Escape-triggered quit confirmation, mid-simulation)

An "Are you sure? Your progress will be lost" dialog, shown when the trainee presses Escape during `ScenarioActive` — not during the Welcome/Scenario Selection flow, which already has its own Exit button and never shows this. Yes quits the app exactly like the Welcome Screen's Exit button; No closes the dialog and hands control straight back.

1. Add Component → `QuitConfirmationController`, anywhere convenient (e.g. alongside `ActionMenuController`).
2. **Confirmation Panel**: drag in the whole dialog's root GameObject (the box with the message and both buttons) — it's set inactive automatically in `Awake()`, regardless of how it was left in the Editor.
3. **Yes Button** / **No Button**: drag in the dialog's two buttons.
4. **Player Controller**: drag in the scene's `PlayerController` — same field/behavior as `ActionMenuController`'s and `WelcomeScreenController`'s own copies.
5. **Crosshair**: optional, same idea as the other two controllers' Crosshair field.

**How it behaves:** pressing Escape mid-simulation opens the dialog, unlocks the mouse, and disables movement/rotation — exactly like the action menu opening. Escape again (or clicking No) closes it and hands control straight back. Clicking Yes quits. It refuses to open at all if the action menu or the calibration UI is already up (so at most one modal is ever active), and — like those two — is checked by `SofiaPersona`/`VirtualAssistantPersona`'s hover-to-talk guard and by `ActionMenuController.Update()`, so it can't be talked or right-clicked through either.

**Test:** mid-simulation, press Escape → confirm the dialog appears, the cursor frees up, and movement/rotation/hover-to-talk/right-click-menu all stop responding to the world behind it. Click No → confirm everything resumes exactly as it was. Press Escape again, click Yes → confirm the app quits (or Play mode stops, in the Editor). Also confirm Escape does nothing while the action menu or the Welcome/Scenario Selection UI is already open, rather than stacking a second dialog on top.

---

## Part 16: IncorrectActionController (deterministic "wrong action" notice)

Shows an "Okay"-only notice whenever `PatientScenarioController.ReportTraineeAction()` rejects a trainee's chosen action - either the current stage doesn't accept action-based advancement at all, or the specific action doesn't match what this stage expects (e.g. picking "Give the injection" before the stage that expects it). Freezes movement/rotation/interactions exactly like the quit confirmation; the single Okay button just dismisses it and hands control back.

1. Add Component → `IncorrectActionController`, anywhere convenient.
2. **Notification Panel**: drag in the whole notice's root GameObject — set inactive automatically in `Awake()`, regardless of how it was left in the Editor.
3. **Okay Button**: drag in the notice's single button.
4. **Player Controller**: drag in the scene's `PlayerController` — same field/behavior as the other three world-interaction-locking controllers.
5. **Crosshair**: optional, same idea as the others' Crosshair field.

**How it behaves:** it subscribes to `PatientScenarioController.Instance.OnIncorrectTraineeAction` in its own `Start()` (not `Awake()` — see the in-code comment on why that ordering matters), so it needs a `PatientScenarioController` present in the scene; a missing one logs a clear Console error at startup rather than the notice just silently never appearing. It has no Escape/manual-trigger path of its own - it only ever opens in response to that event, and like the quit confirmation and calibration UI, it's checked by `SofiaPersona`/`VirtualAssistantPersona`'s hover-to-talk guard and by `ActionMenuController.Update()`, so it can't be talked or right-clicked through either.

**One subtlety worth knowing if you ever touch `ActionMenuController.CloseMenu()`:** picking a wrong action from the action menu triggers this notice *before* the menu finishes closing (`TriggerAction()` runs before `CloseMenu()` in `OnActionSelected()`), so `CloseMenu()` now checks `IncorrectActionController.IsOpen` before releasing its own world-interaction lock - otherwise the menu closing would immediately undo the freeze this notice just applied, in the same frame, before the trainee ever saw it. This is already handled in the shipped code; just don't remove that check if `CloseMenu()` is ever refactored.

**Test:** trigger a wrong action from the action menu (pick an action id that isn't the current stage's expected one, or any action on a stage with no action-based advancement configured at all) → confirm the notice appears immediately (not after a one-frame flicker of regained control), movement/rotation/hover-to-talk/right-click all stop, and the Console logs `[IncorrectActionController] Incorrect action "..." - showing notice.` Click Okay → confirm everything resumes exactly as it was, with no double-suspend/double-release side effects from the action menu having also just closed.

---

## Suggested Hierarchy Summary

A reasonable scene layout, gathering everything above:

```
Bootstrap
 ├─ RemoteOpenAiTokenLoader
 ├─ CinematicCatalog
 ├─ ToolsActionsCatalog
 ├─ RemoteToolsActionsLoader
 ├─ RemoteScenarioApiLoader
 └─ PositionMarkerRegistry (or its own top-level object)

PatientScenarioController
 └─ (component only — can live on Bootstrap instead)

Sofia
 ├─ LlmAgent, SpeechToTextAgent, TextToSpeechAgent (MBB)
 ├─ SofiaPersona
 └─ Animator

VirtualAssistant
 ├─ LlmAgent, SpeechToTextAgent, TextToSpeechAgent (MBB, own instances - not shared with Sofia)
 └─ VirtualAssistantPersona

Player
 ├─ CharacterController
 ├─ PlayerController
 └─ Camera (child, view transform)

CinematicController (VideoPlayer auto-added)

ActionMenuController

QuitConfirmationController

DentalChair (example prop)
 ├─ Collider
 └─ InteractableActions (recline_chair / set_upright, ...)

Canvas
 ├─ SubtitleText (TMP) [+ SubtitleController]
 ├─ CutsceneOverlay (inactive)
 │   └─ VideoImage (RawImage)
 ├─ ActionMenuBackdrop (inactive, full-screen Button)
 ├─ ActionMenuPanel (inactive) [+ ActionMenuUI on the Canvas or nearby]
 │   └─ ActionButtonTemplate (inactive)
 ├─ QuitConfirmationPanel (inactive) [+ QuitConfirmationController, anywhere convenient]
 │   ├─ YesButton
 │   └─ NoButton
 ├─ IncorrectActionPanel (inactive) [+ IncorrectActionController, anywhere convenient]
 │   └─ OkayButton
 ├─ WelcomePanel (active by default) [+ WelcomeScreenController, anywhere convenient]
 │   ├─ StartButton (opens Scenario Selection)
 │   └─ ExitButton
 └─ ScenarioSelectionPanel (inactive by default) [+ ScenarioSelectionController]
     ├─ ScenarioDropdown (TMP_Dropdown)
     ├─ DescriptionField (TMP_InputField, read-only)
     ├─ UseServerTokenToggle
     ├─ CancelButton
     └─ StartButton (launches the simulation)

SessionManager (persistent, DontDestroyOnLoad)
```

---

## First-Run Test Checklist

Work through these in order — each one isolates a different part of the chain, so a failure tells you specifically where to look:

1. **Token bootstrap**: Press Play, check the Console for `[RemoteOpenAiTokenLoader] OpenAI token fetched from backend.` If it errors, check the Flask server is actually running at the `BackendConfig` URL.
2. **Stage content**: Check for `[RemoteScenarioApiLoader] Loaded N stage(s) for scn_id=1 from the backend.` If it falls back instead, check the 404/error message against your Flask server's logs.
3. **Scenario starts**: With `SessionManager.startingState = ScenarioActive`, confirm Sofia's persona actually updates (`[SofiaPersona] Stage updated -> ...` in the Console) as soon as Play begins.
4. **Talk to Sofia**: hover + hold left-click on her, speak, confirm a subtitle appears for both your speech and her reply, and she responds in character.
5. **Talk to the bot**: hover + hold left-click on the Virtual Assistant, ask something open-ended like "what should I do now?" — confirm it answers based on that stage's hint content (not a verbatim recital) and shows a subtitle labeled "Virtual Assistant." Ask again 2-3 times on the same stage and confirm the guidance gets noticeably more direct each time (the escalation behaviour) rather than repeating the same sentence.
6. **Confirmation-by-bot stage**: on a stage with `advanceConfirmationHandledByBot` true, say the confirmation phrase to the bot and confirm it replies with the fixed line AND the scenario advances to the next stage.
7. **Trainee-action stage**: hover a prop with `InteractableActions` (e.g. the dental chair), right-click, confirm its action list pops up where you clicked, left-click one — confirm the menu closes, the console logs `[ActionMenuController] Trainee chose "..."`, and (if that action id matches the current stage's) the stage advances. Also confirm: the mouse cursor is free to move while the menu is up (not locked/spun into a camera look), right-clicking a *different* prop while the menu is already open does nothing until you close it, clicking outside the menu or pressing Escape closes it without triggering anything, and the cursor/camera-look resumes normally afterward either way.
8. **Cutscene**: on a stage with a `cinematicId`, confirm the letterbox overlay appears, the video plays, movement/talk are disabled during it, and everything resumes correctly afterward.
9. **Pose/position**: confirm Sofia visibly teleports/animates correctly on a stage with a `sofiaPositionMarkerId` set.
10. **Debug key**: press `N` at any point and confirm it force-advances regardless of what's currently happening — useful for skipping past a broken stage while debugging the rest of the chain.
11. **Quit confirmation (Part 15)**: mid-simulation, press Escape → confirm the dialog appears, cursor unlocks, and movement/hover-to-talk/right-click all stop. Click No → confirm full control returns. Press Escape, click Yes → confirm the app quits/Play mode stops. Confirm Escape does nothing while the action menu or the calibration UI is already open.
12. **Incorrect-action notice (Part 16)**: pick a wrong action from the action menu → confirm the notice appears immediately (no flicker of regained control), movement/hover-to-talk/right-click all stop, and the Console logs the incorrect-action line. Click Okay → confirm full control returns.
13. **Calibration UI (Part 14)**: Press Play, confirm the Welcome Screen shows immediately and the player can't move/look around behind it (movement/rotation should do nothing, only the mouse cursor should respond). Click Start → confirm Scenario Selection appears, the dropdown populates with every `Published` scenario from the backend, the description field updates (read-only — try clicking into it and confirm you can't type), and — if wired — the bot reads the first scenario's description aloud automatically. Change the dropdown selection a few times and confirm the description and the bot's readback both update each time. Click Cancel → confirm it returns to the Welcome Screen (still locked, not the simulation). Click Start again, pick a scenario, toggle "Use server's AI token" off, click Scenario Selection's own Start → confirm both panels close, the camera/movement immediately respond to input again, the Console shows `[RemoteScenarioApiLoader] Loaded N stage(s) for scn_id=...` for the CHOSEN scenario (not whatever was left in the Inspector), and the first stage begins exactly as in test 3 above. Repeat once more with the checkbox ON and confirm `[RemoteOpenAiTokenLoader] OpenAI token fetched from backend.` appears this time (it should NOT have appeared during the checkbox-off run).
