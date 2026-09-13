using System;
using System.Collections.Generic;
using UnityEngine;

// Drop this on any interactable prop that has more than one pre-built mesh/visual for
// different physical positions - e.g. the dental chair's headrest, prepared as two separate
// meshes (one reclined, one upright) rather than an animated rig - and wants whichever one
// matches the trainee's most recent right-click-menu choice shown, with every other one
// hidden. This is a straight exclusive on/off swap driven by GameObject.SetActive(), not an
// Animator - the simplest option when the "pose change" is really just "which of these two
// (or more) pre-placed meshes is currently visible."
//
// Deliberately separate from InteractableActions/PatientScenarioController's own action-
// dispatch/stage-advancement logic: this only cares "was one of MY listed actions chosen,"
// via InteractableActions.OnActionTriggered (see that class's own comment on the event) -
// it does not care whether that action also happened to advance the current stage. A
// trainee should be able to toggle the headrest back and forth for as long as they want on
// the SAME stage, well before (or after) whichever specific action id is the one that's
// currently wired to advance anything - the visual swap and the scenario-progression
// decision are two different concerns that happen to share the same trigger action ids.
public class ActionVisualSwitcher : MonoBehaviour
{
    [Serializable]
    public class ActionVisual
    {
        [Tooltip("Must exactly match one of this object's InteractableActions entries' Action Id " +
                 "(case-insensitive) - e.g. \"recline_chair\"/\"set_upright\". When the trainee picks " +
                 "that action from the right-click menu, the GameObject below is shown and every " +
                 "OTHER entry's GameObject in this list is hidden.")]
        public string actionId;

        [Tooltip("The pre-built mesh/GameObject to show when this action is chosen - e.g. the " +
                 "reclined headrest mesh for \"recline_chair\", the upright headrest mesh for " +
                 "\"set_upright\". Every other entry's GameObject is deactivated at the same time, " +
                 "so exactly one is ever active.")]
        public GameObject visual;
    }

    [Tooltip("Which InteractableActions this reacts to. Leave empty to use one on this same " +
             "GameObject (the usual case - this script and InteractableActions normally live " +
             "together on the same prop).")]
    [SerializeField] private InteractableActions interactableActions;

    [Tooltip("One entry per visual state this object can switch between - e.g. one row for " +
             "\"recline_chair\" pointing at the reclined headrest mesh, one row for \"set_upright\" " +
             "pointing at the upright headrest mesh. Not limited to two - list as many as you have " +
             "prepared meshes for, if a prop ever needs more than two positions.")]
    [SerializeField] private List<ActionVisual> visuals = new();

    [Tooltip("Index into Visuals above that should be showing as soon as the scene starts, before " +
             "the trainee has chosen anything yet - e.g. 1 if the chair should start upright and " +
             "that's the second entry in the list. Leave at -1 to touch nothing at Start() and just " +
             "trust whatever's already active/inactive on each GameObject as authored in the Editor.")]
    [SerializeField] private int startingVisualIndex = -1;

    private void OnEnable()
    {
        if (interactableActions == null)
            interactableActions = GetComponent<InteractableActions>();

        if (interactableActions == null)
        {
            Debug.LogWarning($"[ActionVisualSwitcher] \"{name}\" has no InteractableActions assigned " +
                               "and none on this GameObject - it will never receive an action to react to.");
            return;
        }

        interactableActions.OnActionTriggered += HandleActionTriggered;
    }

    private void OnDisable()
    {
        if (interactableActions != null)
            interactableActions.OnActionTriggered -= HandleActionTriggered;
    }

    private void Start()
    {
        if (startingVisualIndex >= 0 && startingVisualIndex < visuals.Count)
            ShowOnly(startingVisualIndex);
    }

    private void HandleActionTriggered(string actionId)
    {
        for (int i = 0; i < visuals.Count; i++)
        {
            var entry = visuals[i];
            if (entry != null && string.Equals(entry.actionId?.Trim(), actionId?.Trim(),
                                                 StringComparison.OrdinalIgnoreCase))
            {
                ShowOnly(i);
                return;
            }
        }

        // Not one of ours - not an error. A prop can offer actions this script has no visual
        // entry for at all (e.g. a future non-visual action on the same chair), so silently
        // doing nothing here is correct, not a missed case.
    }

    private void ShowOnly(int index)
    {
        for (int i = 0; i < visuals.Count; i++)
        {
            if (visuals[i]?.visual != null)
                visuals[i].visual.SetActive(i == index);
        }

        Debug.Log($"[ActionVisualSwitcher] \"{name}\" now showing the visual for action " +
                   $"\"{visuals[index].actionId}\".");
    }
}
