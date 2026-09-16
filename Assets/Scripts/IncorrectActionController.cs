using UnityEngine;
using UnityEngine.UI;

// Deterministic "that's not the right action right now" notice. Shown whenever
// PatientScenarioController rejects a trainee-performed action (right-click action menu ->
// InteractableActions.TriggerAction() -> PatientScenarioController.ReportTraineeAction()) -
// either because the current stage doesn't accept any action-based advancement at all, or
// because the specific action id doesn't match what this stage expects. Both cases are treated
// as "wrong action" here; PatientScenarioController itself has no idea this UI exists, raising a
// plain event instead (OnIncorrectTraineeAction) - the same separation SofiaPersona/
// ScenarioSelectionController already keep from whatever reacts to their own events.
//
// Follows the exact same modal-lock pattern as ActionMenuController's action menu (AnyMenuOpen),
// WelcomeScreenController's calibration UI (IsCalibrationActive), and
// QuitConfirmationController (IsOpen): a static flag, checked by every other world-interaction
// script, backed by the same SetWorldInteractionSuspended() shape as those three. See
// CS-37_Project_Analysis.md for the fuller writeup of why a genuine ordering conflict with
// ActionMenuController.CloseMenu() had to be handled explicitly there, not here.
public class IncorrectActionController : MonoBehaviour
{
    public static bool IsOpen { get; private set; }

    [Header("Panel")]
    [Tooltip("The \"that's not the right action right now\" notice panel.")]
    [SerializeField] private GameObject notificationPanel;
    [SerializeField] private Button okayButton;

    [Header("World interaction lock (held while this panel is open)")]
    [Tooltip("The scene's player rig - disabled while this notice is up, exactly like the action " +
             "menu/quit confirmation/calibration UI already disable it around their own UI.")]
    [SerializeField] private PlayerController playerController;
    [Tooltip("Optional on-screen aiming reticle/crosshair - hidden for the same duration.")]
    [SerializeField] private GameObject crosshair;

    private bool isSubscribed;

    private void Awake()
    {
        if (okayButton != null)
            okayButton.onClick.AddListener(Dismiss);
        else
            Debug.LogError("[IncorrectActionController] No Okay Button assigned - dismissing the notice will do nothing.");

        // Hidden from the start regardless of whatever state it was left in in the Editor.
        if (notificationPanel != null) notificationPanel.SetActive(false);
    }

    private void Start()
    {
        // Subscribing here, not in Awake(): Unity doesn't guarantee Awake() order across
        // independent scripts, but every Awake() - including PatientScenarioController's own,
        // which is what sets Instance - is guaranteed to have already run by the time ANY
        // Start() runs. Subscribing in Awake() would race PatientScenarioController's own Awake()
        // and could silently miss the assignment, the same class of bug already hit once in this
        // project (section 36's unassigned-Sofia-Persona NullReferenceException).
        if (PatientScenarioController.Instance != null)
        {
            PatientScenarioController.Instance.OnIncorrectTraineeAction += HandleIncorrectAction;
            isSubscribed = true;
        }
        else
        {
            Debug.LogError("[IncorrectActionController] No PatientScenarioController in the scene at Start() - " +
                             "this notice will never show for any trainee action.");
        }
    }

    private void OnDestroy()
    {
        if (isSubscribed && PatientScenarioController.Instance != null)
            PatientScenarioController.Instance.OnIncorrectTraineeAction -= HandleIncorrectAction;

        if (okayButton != null) okayButton.onClick.RemoveListener(Dismiss);

        // Defensive only, mirroring QuitConfirmationController/WelcomeScreenController's
        // identical reasoning - a scene reload/teardown should never leave a stale `true` behind.
        if (IsOpen) IsOpen = false;
    }

    private void HandleIncorrectAction(string actionId)
    {
        // Deliberately does NOT check "is another modal already open" the way
        // QuitConfirmationController.Update() does before opening on Escape. This fires
        // SYNCHRONOUSLY from inside ActionMenuController.OnActionSelected() - TriggerAction()
        // (which raises this) runs BEFORE that method's own CloseMenu() call - so the action
        // menu is technically still "open" for one more line of code at the exact moment this
        // runs. ActionMenuController.CloseMenu() itself checks IncorrectActionController.IsOpen
        // before releasing the world-interaction lock (see its own comment) specifically so this
        // can safely take over the lock mid-close, rather than the trainee seeing control handed
        // back for one frame and immediately taken away again.
        Debug.Log($"[IncorrectActionController] Incorrect action \"{actionId}\" - showing notice.");
        IsOpen = true;
        if (notificationPanel != null) notificationPanel.SetActive(true);
        SetWorldInteractionSuspended(true);
    }

    private void Dismiss()
    {
        IsOpen = false;
        if (notificationPanel != null) notificationPanel.SetActive(false);
        SetWorldInteractionSuspended(false);
    }

    // Same field names and pattern as ActionMenuController/WelcomeScreenController/
    // QuitConfirmationController's own copies - deliberately not shared code between them (see
    // any of their own comments for why: no single "who currently owns the lock" arbiter has
    // been needed, since these modals are mutually exclusive by construction).
    private void SetWorldInteractionSuspended(bool suspended)
    {
        if (playerController != null)
        {
            playerController.enabled = !suspended;
        }
        else
        {
            Debug.LogWarning("[IncorrectActionController] No Player Controller assigned - movement/rotation " +
                               (suspended
                                   ? "cannot be suspended for the incorrect-action notice."
                                   : "cannot be re-enabled after dismissing it - the trainee will be stuck unable to move/look."));
        }

        if (crosshair != null)
        {
            crosshair.SetActive(!suspended);
        }
        else
        {
            Debug.LogWarning("[IncorrectActionController] No Crosshair assigned - the on-screen reticle " +
                               (suspended
                                   ? "cannot be hidden for the incorrect-action notice."
                                   : "cannot be shown again after dismissing it."));
        }

        Cursor.lockState = suspended ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = suspended;
    }
}
