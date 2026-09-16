using UnityEngine;
using UnityEngine.UI;

// Top-level orchestrator for the whole pre-scenario calibration flow: Welcome Screen ->
// Scenario Selection -> commit. Owns the two panels' visibility and the world-interaction
// lock (movement/rotation disabled, mouse-only) for the ENTIRE flow - both screens, not just
// one - since the trainee shouldn't be able to walk/look around the empty simulation room
// while picking a scenario any more than while looking at the title screen. The lock engages
// the instant this component starts and is released only once the trainee commits via
// ScenarioSelectionController's own Start button.
//
// ScenarioSelectionController (a separate component, living on the Scenario Selection panel)
// owns everything specific to THAT screen - the dropdown, the read-only description field,
// the "Use server's AI token" checkbox, reading the description aloud through the bot - and
// only ever talks back to this class through two events (OnCancelRequested/OnStartRequested),
// mirroring how ActionMenuController (orchestration + world-interaction suspension) and
// ActionMenuUI (pure view) are already split in this project.
public class WelcomeScreenController : MonoBehaviour
{
    // Convenience for every world-interaction script (SofiaPersona, VirtualAssistantPersona,
    // ActionMenuController) to check "is the calibration UI currently up" the exact same way
    // they already check ActionMenuController.AnyMenuOpen - added after the team found that
    // simply disabling PlayerController and unlocking the cursor was NOT enough to stop the
    // trainee from talking to Sofia / right-clicking props while this screen was showing.
    // Root cause: PlayerController only owns MOVEMENT/rotation - it has nothing to do with
    // hover-to-talk or the right-click menu, which each do their OWN independent
    // Physics.Raycast from the mouse position in their own Update(), completely unaware of any
    // UI Canvas that might be visually covering the screen (a UI Graphic Raycaster only ever
    // intercepts clicks bound for OTHER UI elements - it has no effect on a plain
    // Physics.Raycast fired by unrelated game code). Without an explicit guard like this one,
    // clicking "through" the Welcome Screen would always have reached whatever 3D collider
    // sits behind it on screen, Canvas or no Canvas, UI raycaster or no UI raycaster.
    public static bool IsCalibrationActive { get; private set; }

    [Header("Panels")]
    [Tooltip("The title screen: \"Welcome to Dental Simulator\", Start/Exit.")]
    [SerializeField] private GameObject welcomePanel;

    [Tooltip("The scenario-selection overlay: dropdown, description, checkbox, Cancel/Start.")]
    [SerializeField] private GameObject scenarioSelectionPanel;

    [Tooltip("OPTIONAL catch-all - only needed if your Welcome/Scenario Selection visuals include " +
             "something that lives OUTSIDE both panels above (e.g. a full-screen background image " +
             "or blur placed directly on the Canvas rather than nested inside Welcome Panel), which " +
             "would otherwise keep showing forever since toggling the two panels above never touches " +
             "it. If assigned, this is deactivated too the instant the trainee commits (HandleFinalStart), " +
             "on top of - not instead of - the two panels above. Leave empty if your whole calibration " +
             "UI (background art included) is already fully nested inside Welcome Panel/Scenario " +
             "Selection Panel, since then toggling those two is already complete.")]
    [SerializeField] private GameObject calibrationRoot;

    [Header("Welcome screen buttons")]
    [Tooltip("\"Start\" on the WELCOME screen - opens Scenario Selection. NOT the same button " +
             "as Scenario Selection's own Start, which actually launches the simulation.")]
    [SerializeField] private Button welcomeStartButton;
    [SerializeField] private Button exitButton;

    [Header("Scenario Selection")]
    [Tooltip("The controller living on scenarioSelectionPanel - this class subscribes to its " +
             "OnCancelRequested/OnStartRequested events rather than owning any dropdown/description " +
             "logic itself.")]
    [SerializeField] private ScenarioSelectionController scenarioSelectionController;

    [Header("World interaction lock (held for the whole calibration flow)")]
    [Tooltip("The scene's player rig - disabled for the whole Welcome/Scenario Selection flow, " +
             "exactly like ActionMenuController already disables it while its own menu is open.")]
    [SerializeField] private PlayerController playerController;
    [Tooltip("Optional on-screen aiming reticle/crosshair - hidden for the same duration, for the " +
             "same reason ActionMenuController hides it around its own menu.")]
    [SerializeField] private GameObject crosshair;

    [Header("Kicked off once the trainee's FINAL Start (on Scenario Selection) fires")]
    [SerializeField] private RemoteScenarioApiLoader remoteScenarioApiLoader;
    [Tooltip("Optional - only actually used if the trainee leaves \"Use server's AI token\" " +
             "checked. Leave unassigned if this build has no server-token option at all yet; a " +
             "checked checkbox with nothing assigned here just logs a warning and does nothing, " +
             "rather than throwing.")]
    [SerializeField] private RemoteOpenAiTokenLoader remoteOpenAiTokenLoader;

    private void Awake()
    {
        // NOTE: every reference to a UnityEngine.Object field below uses an explicit
        // `if (x != null)`, never the `?.` null-conditional operator. This is a real, previously
        // uncaught bug in this file: Unity gives an unassigned/destroyed Object reference a
        // special non-null C# wrapper so its OWN overridden `==`/`!=` operators can detect it and
        // report "null" - but `?.` compiles to a raw reference check that bypasses that override
        // entirely, sees the wrapper as "not null", and calls straight through to it, which then
        // throws UnassignedReferenceException at runtime instead of being safely skipped. This is
        // exactly what caused the "cursor/movement doesn't work, reticle is gone" report: see
        // HandleFinalStart() below.
        if (welcomeStartButton != null)
            welcomeStartButton.onClick.AddListener(ShowScenarioSelection);
        else
            Debug.LogError("[WelcomeScreenController] No Welcome Start Button assigned - the Welcome Screen's Start button won't do anything.");

        if (exitButton != null)
            exitButton.onClick.AddListener(ExitApplication);
        else
            Debug.LogError("[WelcomeScreenController] No Exit Button assigned - the Welcome Screen's Exit button won't do anything.");

        if (scenarioSelectionController != null)
        {
            scenarioSelectionController.OnCancelRequested += ShowWelcome;
            scenarioSelectionController.OnStartRequested += HandleFinalStart;
        }
        else
        {
            Debug.LogError("[WelcomeScreenController] No ScenarioSelectionController assigned - " +
                             "the Scenario Selection panel's Cancel/Start buttons won't do anything.");
        }

        // Start on the Welcome Screen, Scenario Selection hidden - regardless of whatever
        // state these two panels happened to be left in in the Editor.
        if (scenarioSelectionPanel != null) scenarioSelectionPanel.SetActive(false);
        if (welcomePanel != null) welcomePanel.SetActive(true);
    }

    private void OnDestroy()
    {
        if (welcomeStartButton != null) welcomeStartButton.onClick.RemoveListener(ShowScenarioSelection);
        if (exitButton != null) exitButton.onClick.RemoveListener(ExitApplication);

        if (scenarioSelectionController != null)
        {
            scenarioSelectionController.OnCancelRequested -= ShowWelcome;
            scenarioSelectionController.OnStartRequested -= HandleFinalStart;
        }

        // Defensive only - HandleFinalStart() is the normal way this flips back to false.
        // Clearing it here too means a scene reload/teardown can never leave a stale `true`
        // behind and silently block every hover-to-talk/right-click script in a DIFFERENT
        // scene that happens to load next.
        if (IsCalibrationActive) IsCalibrationActive = false;
    }

    private void Start()
    {
        // Locked from the moment this screen appears, not from the moment Scenario Selection
        // opens - the trainee has no legitimate reason to be able to move/look around before
        // they've even chosen a scenario.
        SetWorldInteractionSuspended(true);

        // Same diagnostic ActionMenuController.OpenMenu() already runs before ITS own UI ever
        // takes a click (the exact three things that silently eat a click: no EventSystem, no
        // GraphicRaycaster, or a still-locked cursor) - run once here too, since this is a
        // SEPARATE Canvas from whatever the action menu is parented under, and a GraphicRaycaster
        // is a per-Canvas requirement, not a scene-wide one. A working action menu elsewhere in
        // the scene does NOT guarantee this Canvas has its own.
        if (UnityEngine.EventSystems.EventSystem.current == null)
        {
            Debug.LogError("[WelcomeScreenController] No EventSystem exists in this scene (or the active one is disabled) - " +
                             "no UI Button anywhere, including Start/Exit here, can ever receive a click without one.");
        }

        var raycaster = FindAnyObjectByType<GraphicRaycaster>();
        if (raycaster == null)
        {
            Debug.LogError("[WelcomeScreenController] No GraphicRaycaster found on any Canvas in this scene - UI clicks " +
                             "cannot be detected at all without one. Check the Canvas the Welcome/Scenario Selection panels " +
                             "are on has a Graphic Raycaster component (Canvas usually adds one automatically, but it can be " +
                             "missing on a Canvas created/duplicated a non-standard way, or removed by accident).");
        }
        else if (!raycaster.enabled || !raycaster.gameObject.activeInHierarchy)
        {
            Debug.LogError($"[WelcomeScreenController] Found a GraphicRaycaster on \"{raycaster.name}\" but it's disabled or " +
                             "its GameObject is inactive - re-enable it, UI clicks won't register while it's off.");
        }

        Debug.Log($"[WelcomeScreenController] Welcome Screen shown - Cursor.lockState={Cursor.lockState}, " +
                   $"Cursor.visible={Cursor.visible}, IsCalibrationActive={IsCalibrationActive}. If lockState isn't None " +
                   "here, clicks land wherever the OS cursor actually is (invisible, since it's locked), not on the " +
                   "visible buttons - that alone explains \"can't click Start/Exit\" even if everything else is fine.");

        // Catches the OTHER way the reticle can end up permanently gone after calibration: if
        // whoever wired this in the Inspector nested the crosshair under calibrationRoot (e.g.
        // because both live under the same "UI" parent), HandleFinalStart()'s
        // `calibrationRoot?.SetActive(false)` deactivates the reticle's entire ancestor chain,
        // and the later `crosshair.SetActive(true)` in SetWorldInteractionSuspended(false) cannot
        // undo that - activeInHierarchy requires every ancestor to be active, not just the object
        // itself. This is a config mistake, not something code can route around, so it's called
        // out loudly at startup rather than discovered later as an unexplained missing reticle.
        if (crosshair != null && calibrationRoot != null && crosshair.transform.IsChildOf(calibrationRoot.transform))
        {
            Debug.LogError("[WelcomeScreenController] Crosshair is nested UNDER Calibration Root in the hierarchy. " +
                             "HandleFinalStart() deactivates Calibration Root, which will permanently hide the " +
                             "reticle too (a SetActive(true) on a child can't override an inactive ancestor). Move " +
                             "the reticle out from under Calibration Root, or point Crosshair at a reticle object " +
                             "that lives outside it.");
        }
    }

    private void ShowScenarioSelection()
    {
        welcomePanel.SetActive(false);
        scenarioSelectionPanel.SetActive(true);

        // Fetches the scenario list fresh every time this panel opens rather than once ever -
        // see ScenarioSelectionController.OnPanelShown()'s own comment for why.
        if (scenarioSelectionController != null) scenarioSelectionController.OnPanelShown();
    }

    private void ShowWelcome()
    {
        scenarioSelectionPanel.SetActive(false);
        welcomePanel.SetActive(true);
    }

    private void HandleFinalStart(int scenarioId, bool useServerToken)
    {
        Debug.Log($"[WelcomeScreenController] Trainee committed to scn_id={scenarioId} " +
                  $"(use server AI token: {useServerToken}) - closing calibration UI and starting the simulation.");

        // Both panels explicitly, not just scenarioSelectionPanel - welcomePanel SHOULD already
        // be false by this point (ShowScenarioSelection() turned it off when this screen opened,
        // and nothing since then should have turned it back on), but setting it again here is
        // free insurance against exactly the symptom of "the Welcome Screen is still visibly
        // overlaid after Start" - if that's what you're seeing despite this, the far more likely
        // culprit is calibrationRoot's own scenario above: something in your Canvas (a shared
        // background image, a blur panel) sitting OUTSIDE both of these two GameObjects entirely.
        welcomePanel.SetActive(false);
        scenarioSelectionPanel.SetActive(false);

        // Deliberately NOT `calibrationRoot?.SetActive(false);` - that was the actual bug behind
        // the "cursor/movement doesn't work, reticle is gone" report. calibrationRoot is meant to
        // be left unassigned on builds that don't need it (see its tooltip above), but Unity gives
        // an unassigned Object field a non-null wrapper so its own `==`/`!=` can report "null" -
        // the `?.` operator bypasses that and calls SetActive() on the wrapper anyway, which threw
        // UnassignedReferenceException right here and aborted the REST of this method, including
        // the SetWorldInteractionSuspended(false) call two lines down. That's exactly why movement,
        // the cursor, and the reticle never came back: this method never got that far.
        if (calibrationRoot != null) calibrationRoot.SetActive(false);
        SetWorldInteractionSuspended(false);

        if (useServerToken)
        {
            if (remoteOpenAiTokenLoader != null)
            {
                remoteOpenAiTokenLoader.BeginFetch();
            }
            else
            {
                Debug.LogWarning("[WelcomeScreenController] \"Use server's AI token\" was checked but no " +
                                   "RemoteOpenAiTokenLoader is assigned here - whatever token is already in " +
                                   "CredentialStorage will be used instead, unchanged.");
            }
        }
        // Left unchecked: deliberately do nothing at all - the trainee's own CredentialStorage
        // token is left completely untouched, exactly as specified.

        if (remoteScenarioApiLoader != null)
        {
            remoteScenarioApiLoader.SetScenarioId(scenarioId);
            remoteScenarioApiLoader.BeginLoading();
        }
        else
        {
            Debug.LogError("[WelcomeScreenController] No RemoteScenarioApiLoader assigned - cannot load the chosen scenario's stages.");
        }

        if (SessionManager.Instance != null)
            SessionManager.Instance.SkipToScenarioActive();
        else
            Debug.LogError("[WelcomeScreenController] No SessionManager in the scene - the patient scenario won't start.");
    }

    private void ExitApplication()
    {
        Debug.Log("[WelcomeScreenController] Exit requested - quitting the application.");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // Same pattern, same field names, as ActionMenuController.SetWorldInteractionSuspended() -
    // deliberately not shared code between the two (a shared helper would need a third file
    // and a way to reconcile "who's currently suspending it" if both were ever active at once,
    // which can't happen in practice here: the action menu doesn't exist until well after the
    // scenario is running, long after this screen is gone).
    private void SetWorldInteractionSuspended(bool suspended)
    {
        IsCalibrationActive = suspended;

        // Loud, not silent: these two used to be plain `if (x != null)` no-ops. If either field
        // is left unassigned in the Inspector, movement/rotation and the reticle would appear to
        // work fine while the calibration UI is showing (both start suspended by default anyway)
        // but then NEVER come back after HandleFinalStart() calls this with suspended=false - a
        // "cursor/movement doesn't work, reticle is gone, after calibration closes" bug with zero
        // Console evidence pointing at the cause. Note this is a SEPARATE pair of fields from
        // ActionMenuController's own Player Controller/Crosshair references - wiring one script
        // does not wire the other.
        if (playerController != null)
        {
            playerController.enabled = !suspended;
        }
        else
        {
            Debug.LogWarning("[WelcomeScreenController] No Player Controller assigned - movement/rotation " +
                               (suspended
                                   ? "cannot be suspended for the calibration UI (trainee may be able to walk/look around while it's showing)."
                                   : "cannot be re-enabled now that calibration is done (trainee will be stuck unable to move/look)."));
        }

        if (crosshair != null)
        {
            crosshair.SetActive(!suspended);
        }
        else
        {
            Debug.LogWarning("[WelcomeScreenController] No Crosshair assigned - the on-screen reticle " +
                               (suspended
                                   ? "cannot be hidden for the calibration UI."
                                   : "cannot be shown again now that calibration is done. If you DO have a reticle object " +
                                     "and it's still just missing, check whether it's a child of the optional Calibration " +
                                     "Root field above instead - SetActive(true) on a child can't make it visible while an " +
                                     "ancestor GameObject is inactive, and calibrationRoot is deactivated in HandleFinalStart()."));
        }

        Cursor.lockState = suspended ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = suspended;
    }
}
