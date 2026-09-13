using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Replaces TraineeActionTrigger (which offered exactly one action, fired directly by
// whatever UI/event you wired to it) with the richer flow the team wants: right-click (or,
// on XR later, a controller button - see ActionMenuController's class comment) a hovered
// object to pop up a list of the things you can DO with it, then left-click one to do it.
// Drop this on any interactable prop - a dental chair, a tray of instruments, a defib unit -
// and list the actions it offers. ActionMenuController does the actual hover/right-click/menu
// work; this class just owns the prop's own data (what actions it has) and the final
// dispatch once one's chosen.
public class InteractableActions : MonoBehaviour
{
    [Serializable]
    public class ActionOption
    {
        [Tooltip("Must exactly match one of the current stage's Advance Trigger Action Ids " +
                 "(case-insensitive) for choosing this option to actually advance anything - " +
                 "e.g. \"recline_chair\". Same string a tutor would type into the backend's " +
                 "act_id column.")]
        public string actionId;

        [Tooltip("What the trainee sees in the action-menu list, e.g. \"Recline it\". Keep it " +
                 "short - this is a menu label, not a description.")]
        public string label;
    }

    [Tooltip("Every action this object can offer via right-click. ActionMenuController reads " +
             "this list to populate the menu - order here is menu order.")]
    [SerializeField] private List<ActionOption> actions = new();

    [Tooltip("Collider ActionMenuController's hover-raycast should hit to recognize THIS object. " +
             "Leave empty to use a Collider on this same GameObject.")]
    [SerializeField] private Collider hoverCollider;

    [Tooltip("Leave empty to use PatientScenarioController.Instance (the usual case). Only assign " +
             "explicitly if your scene has a reason to route a specific prop to a specific " +
             "controller instance.")]
    [SerializeField] private PatientScenarioController patientScenarioController;

    [Tooltip("Optional - if assigned, every actionId above is checked against the backend's known " +
             "action ids (GET /api/tools-actions) at startup, and a warning is logged for any that " +
             "don't match. Catches a typo here long before a trainee ever opens this object's menu.")]
    [SerializeField] private ToolsActionsCatalog toolsActionsCatalog;

    public IReadOnlyList<ActionOption> Actions => actions;

    public Collider HoverCollider => hoverCollider != null ? hoverCollider : GetComponent<Collider>();

    // Raised whenever a VALID action of THIS object's own is chosen - i.e. after the same
    // isOwnAction check TriggerAction() already does for PatientScenarioController.
    // Deliberately independent of whether that action happens to also advance the current
    // stage (ReportTraineeAction() below decides that separately) - a purely visual reaction
    // to "the trainee picked this option" (e.g. swapping which of two prepared meshes is
    // active, see ActionVisualSwitcher) shouldn't depend on scenario-progression logic at
    // all, and should still fire even on a stage where this action id isn't the one that's
    // currently allowed to advance anything.
    public event Action<string> OnActionTriggered;

    private void Start()
    {
        if (hoverCollider == null && GetComponent<Collider>() == null)
        {
            Debug.LogWarning($"[InteractableActions] \"{name}\" has no hoverCollider assigned and no " +
                              "Collider on this GameObject - ActionMenuController will never be able " +
                              "to detect it as hovered.");
        }

        if (toolsActionsCatalog == null) return;

        foreach (var option in actions)
        {
            if (option == null || string.IsNullOrWhiteSpace(option.actionId)) continue;

            if (!toolsActionsCatalog.IsKnownActionId(option.actionId))
            {
                Debug.LogWarning($"[InteractableActions] \"{name}\"'s action \"{option.actionId}\" doesn't " +
                                   "match any action id the backend reported via /api/tools-actions - check for a typo.");
            }
        }
    }

    // Called by ActionMenuController once the trainee has picked an option from this
    // object's menu. Re-validates the id is actually one of THIS object's own offered
    // actions (defense in depth - a menu should never be able to fire an id it didn't
    // itself list) before forwarding to the same deterministic path every other trigger
    // source uses.
    public void TriggerAction(string actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId))
        {
            Debug.LogError($"[InteractableActions] \"{name}\".TriggerAction() called with a blank actionId.");
            return;
        }

        bool isOwnAction = actions.Any(a => a != null &&
            string.Equals(a.actionId?.Trim(), actionId.Trim(), StringComparison.OrdinalIgnoreCase));

        if (!isOwnAction)
        {
            Debug.LogError($"[InteractableActions] \"{name}\" was asked to trigger \"{actionId}\", which " +
                             "isn't one of its own listed actions - ignoring.");
            return;
        }

        OnActionTriggered?.Invoke(actionId);

        PatientScenarioController controller = patientScenarioController != null
            ? patientScenarioController
            : PatientScenarioController.Instance;

        if (controller == null)
        {
            Debug.LogError($"[InteractableActions] No PatientScenarioController available for \"{name}\" - " +
                             "assign one explicitly, or make sure one exists in the scene.");
            return;
        }

        controller.ReportTraineeAction(actionId);
    }
}
