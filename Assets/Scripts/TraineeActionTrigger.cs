using UnityEngine;

// SUPERSEDED - kept only for reference / any scene still wired to it. Replaced by
// InteractableActions.cs + ActionMenuController.cs, which support a PROP offering multiple
// actions via a right-click popup menu (hover -> right-click -> pick one -> left-click),
// rather than this class's one-action-per-prop-fired-by-whatever-UI-you-wire-up-yourself
// model. There was no real interactive UI driving Trigger() yet, so this is a straight
// replacement, not a parallel option - new props should use InteractableActions instead.
// Safe to delete once nothing in the scene references this anymore.
//
// Original comment, for context: drop this on any interactable prop that represents a
// gradeable trainee ACTION rather than something they say - a syringe, a chair-recline
// lever, a defibrillator, etc. This is trigger path (b) from the transition-flow spec,
// parallel to the conversation-driven path (a) SofiaPersona already handles; both end up
// going through the exact same deterministic PatientScenarioController.AdvanceStage(), just
// detected differently. Wire Trigger() to whatever actually represents "the trainee did
// this" for the specific object it's on - a Button.onClick, an XR Interactable's
// onSelectEntered/onActivated, an Animation Event fired at the exact moment a "give
// injection" animation lands, etc.
public class TraineeActionTrigger : MonoBehaviour
{
    [Tooltip("Must exactly match one of the current stage's Advance Trigger Action Ids " +
             "(case-insensitive) for this to actually advance anything - e.g. \"give_injection\", " +
             "\"recline_chair\". Same string a tutor would type into the CSV/spreadsheet's " +
             "advanceTriggerActionIds column.")]
    [SerializeField] private string actionId;

    [Tooltip("Leave empty to use PatientScenarioController.Instance (the usual case - there's " +
             "normally exactly one in the scene). Only assign this explicitly if your scene has " +
             "a reason to route specific props to a specific controller instance.")]
    [SerializeField] private PatientScenarioController patientScenarioController;

    [Tooltip("Optional - if assigned, actionId is checked against the backend's known action " +
             "ids (GET /api/tools-actions) at startup, and a warning is logged if it doesn't " +
             "match anything. Catches a typo here long before a trainee ever interacts with " +
             "this prop. Purely a startup sanity check - has no effect on Trigger() itself.")]
    [SerializeField] private ToolsActionsCatalog toolsActionsCatalog;

    private void Start()
    {
        if (toolsActionsCatalog != null && !toolsActionsCatalog.IsKnownActionId(actionId))
        {
            Debug.LogWarning($"[TraineeActionTrigger] \"{name}\"'s actionId \"{actionId}\" doesn't match any " +
                               "action id the backend reported via /api/tools-actions - check for a typo.");
        }
    }

    public void Trigger()
    {
        PatientScenarioController controller = patientScenarioController != null
            ? patientScenarioController
            : PatientScenarioController.Instance;

        if (controller == null)
        {
            Debug.LogError($"[TraineeActionTrigger] No PatientScenarioController available for \"{name}\" - " +
                             "assign one explicitly, or make sure one exists in the scene.");
            return;
        }

        if (string.IsNullOrWhiteSpace(actionId))
        {
            Debug.LogError($"[TraineeActionTrigger] \"{name}\" has no actionId set - nothing to report.");
            return;
        }

        controller.ReportTraineeAction(actionId);
    }
}
