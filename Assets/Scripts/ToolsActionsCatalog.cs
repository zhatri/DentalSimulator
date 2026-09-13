using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Runtime copy of the backend's TOOL/TOOL_ACTION join (GET /api/tools-actions) - which
// action ids exist and which tool each belongs to. Not required for the core
// trigger-advance flow (that's driven purely by matching a stage's own
// advanceTriggerActionIds against one of an InteractableActions object's own action ids -
// see PatientScenarioController.ReportTraineeAction), but wiring one up lets
// InteractableActions warn at Start if any of its actionIds isn't something the backend
// actually knows about, catching a typo in a prop's Inspector field long before a trainee
// ever opens that object's menu.
public class ToolsActionsCatalog : MonoBehaviour
{
    [Serializable]
    public class Entry
    {
        public int toolId;
        public string toolName;
        public string actionId;
    }

    private readonly List<Entry> entries = new();
    private HashSet<string> knownActionIds = new(StringComparer.OrdinalIgnoreCase);

    public void SetEntries(List<Entry> newEntries)
    {
        entries.Clear();
        if (newEntries != null) entries.AddRange(newEntries);

        knownActionIds = new HashSet<string>(
            entries.Where(e => !string.IsNullOrWhiteSpace(e.actionId)).Select(e => e.actionId.Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    // Returns true if this actionId is a known one from the backend, OR if the catalog
    // simply hasn't loaded anything yet (empty) - so a validation check against this never
    // produces a false "unknown" warning purely because the fetch hasn't completed/hasn't
    // been wired up in this scene.
    public bool IsKnownActionId(string actionId)
    {
        if (entries.Count == 0) return true;
        return !string.IsNullOrWhiteSpace(actionId) && knownActionIds.Contains(actionId.Trim());
    }

    public IReadOnlyList<Entry> Entries => entries;
}
