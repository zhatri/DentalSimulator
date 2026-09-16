using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Escape-triggered "are you sure you want to quit" confirmation, shown mid-simulation. Follows
// the exact same "modal owns the world-interaction lock" pattern already established twice in
// this project - ActionMenuController's action menu (AnyMenuOpen) and WelcomeScreenController's
// calibration UI (IsCalibrationActive) - rather than inventing a new one: a static IsOpen flag,
// checked by SofiaPersona/VirtualAssistantPersona's hover-to-talk guard and by
// ActionMenuController.Update(), so this dialog can't be talked/right-clicked through the same
// way earlier modals in this project couldn't be (see CS-37_Project_Analysis.md section 46).
//
// This class does NOT attempt to also become a fourth thing every other modal checks - instead
// it refuses to open at all while ActionMenuController's menu or the calibration UI already own
// the lock (see Update() below), so at most one modal is ever active at a time, by construction,
// the same mutual-exclusion approach already used elsewhere rather than a new arbitration scheme.
public class QuitConfirmationController : MonoBehaviour
{
    public static bool IsOpen { get; private set; }

    [Header("Panel")]
    [Tooltip("The \"Your progress will be lost if you quit now. Are you sure?\" panel.")]
    [SerializeField] private GameObject confirmationPanel;
    [SerializeField] private Button yesButton;
    [SerializeField] private Button noButton;

    [Header("World interaction lock (held while this panel is open)")]
    [Tooltip("The scene's player rig - disabled while this confirmation is up, exactly like " +
             "ActionMenuController/WelcomeScreenController already disable it around their own UI.")]
    [SerializeField] private PlayerController playerController;
    [Tooltip("Optional on-screen aiming reticle/crosshair - hidden for the same duration, for the " +
             "same reason the action menu and calibration UI hide it.")]
    [SerializeField] private GameObject crosshair;

    private void Awake()
    {
        if (yesButton != null)
            yesButton.onClick.AddListener(ConfirmQuit);
        else
            Debug.LogError("[QuitConfirmationController] No Yes Button assigned - confirming quit will do nothing.");

        if (noButton != null)
            noButton.onClick.AddListener(ResumeSimulation);
        else
            Debug.LogError("[QuitConfirmationController] No No Button assigned - dismissing the dialog will do nothing.");

        // Hidden from the start regardless of whatever state it was left in in the Editor -
        // same defensive reasoning WelcomeScreenController's Awake() already uses for its panels.
        if (confirmationPanel != null) confirmationPanel.SetActive(false);
    }

    private void OnDestroy()
    {
        if (yesButton != null) yesButton.onClick.RemoveListener(ConfirmQuit);
        if (noButton != null) noButton.onClick.RemoveListener(ResumeSimulation);

        // Defensive only, mirroring WelcomeScreenController.OnDestroy()'s identical reasoning -
        // a scene reload/teardown should never be able to leave a stale `true` behind and
        // silently block hover-to-talk/the action menu in whatever scene loads next.
        if (IsOpen) IsOpen = false;
    }

    private void Update()
    {
        if (Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame) return;

        if (IsOpen)
        {
            // Pressing Escape again while already open reads as "never mind" - the same
            // dismiss action as clicking No, not a second, redundant confirmation step.
            ResumeSimulation();
            return;
        }

        // Refuse to open on top of another modal that already owns the world-interaction lock
        // and cursor state (the action menu, or the pre-scenario calibration UI) - popping a
        // second, unrelated modal over either would fight them for the same lock rather than
        // cleanly replacing it. ActionMenuController.Update() already consumes Escape itself
        // while ITS menu is open (to close that menu), so this class deliberately never sees
        // that keypress in the first place for that case - the AnyMenuOpen check below is a
        // second line of defense in case that ever changes.
        if (ActionMenuController.AnyMenuOpen || WelcomeScreenController.IsCalibrationActive
            || IncorrectActionController.IsOpen) return;

        OpenConfirmation();
    }

    private void OpenConfirmation()
    {
        IsOpen = true;
        if (confirmationPanel != null) confirmationPanel.SetActive(true);
        SetWorldInteractionSuspended(true);
    }

    private void ResumeSimulation()
    {
        IsOpen = false;
        if (confirmationPanel != null) confirmationPanel.SetActive(false);
        SetWorldInteractionSuspended(false);
    }

    private void ConfirmQuit()
    {
        Debug.Log("[QuitConfirmationController] Quit confirmed - quitting the application.");
        // Same pattern as WelcomeScreenController.ExitApplication() - stop Play mode in the
        // Editor, actually quit in a real build.
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // Same field names and pattern as ActionMenuController.SetWorldInteractionSuspended() and
    // WelcomeScreenController.SetWorldInteractionSuspended() - deliberately not shared code
    // between the three (see either of their own comments for why: no single "who currently owns
    // the lock" arbiter exists, and none has been needed since these modals are already mutually
    // exclusive by construction via the guard in Update() above).
    private void SetWorldInteractionSuspended(bool suspended)
    {
        if (playerController != null)
        {
            playerController.enabled = !suspended;
        }
        else
        {
            Debug.LogWarning("[QuitConfirmationController] No Player Controller assigned - movement/rotation " +
                               (suspended
                                   ? "cannot be suspended for the quit confirmation."
                                   : "cannot be re-enabled after dismissing it - the trainee will be stuck unable to move/look."));
        }

        if (crosshair != null)
        {
            crosshair.SetActive(!suspended);
        }
        else
        {
            Debug.LogWarning("[QuitConfirmationController] No Crosshair assigned - the on-screen reticle " +
                               (suspended
                                   ? "cannot be hidden for the quit confirmation."
                                   : "cannot be shown again after dismissing it."));
        }

        Cursor.lockState = suspended ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = suspended;
    }
}
