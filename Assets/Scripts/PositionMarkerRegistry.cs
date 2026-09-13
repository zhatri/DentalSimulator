using System;
using System.Collections.Generic;
using UnityEngine;

// Named scene positions (standing at the clinic door, seated in the dental chair, reclined
// unconscious in the dental chair, etc.) that PatientStageDefinition.sofiaPositionMarkerId
// refers to by string id. Exists because a ScriptableObject asset (like
// PatientStageDefinition) cannot hold a direct reference to a scene GameObject/Transform -
// only a component living in the scene, like this one, can. Keeping stage data
// scene-independent this way is also what lets it stay spreadsheet/API-authorable (see
// RemoteScenarioLoader/RemoteScenarioApiLoader) - a tutor's data can say "chair_reclined"
// without knowing or caring where that empty GameObject actually sits in the Hierarchy.
//
// Each marker also optionally carries an Animator TRIGGER, not just a position - this
// matches the backend's actual POSE table (Database.xlsx: pos_id -> pos_name, ONE id for
// a combined "where + what pose" concept, e.g. pos_name "chair_seated_unconscious" implies
// both a place AND an animation state) rather than treating position and pose as two
// independent things. PatientScenarioController.ApplySofiaPoseAndPosition() fires this
// marker's own animatorTrigger automatically whenever a stage's sofiaPositionMarkerId
// resolves to one - a stage's own (optional) sofiaPoseTrigger field still exists for
// hand-authored ScriptableObject stages that want to set an animation trigger WITHOUT also
// moving Sofia, and takes priority if both happen to be set.
public class PositionMarkerRegistry : MonoBehaviour
{
    [Serializable]
    public class Marker
    {
        [Tooltip("Matches a PatientStageDefinition.sofiaPositionMarkerId exactly (case-insensitive) - " +
                 "this is also where the backend's POSE.pos_name (or pos_id, as a fallback) lands, " +
                 "e.g. \"chair_seated_unconscious\".")]
        public string id;

        [Tooltip("An empty GameObject placed exactly where Sofia should be teleported to for this id - " +
                 "its rotation is applied too, so face it the direction she should be facing.")]
        public Transform transform;

        [Tooltip("Optional Animator trigger to fire at the same time Sofia is moved to this marker " +
                 "(e.g. \"GoUnconscious\"). Leave blank if this marker is a pure position with no " +
                 "associated pose change.")]
        public string animatorTrigger;
    }

    [SerializeField] private List<Marker> markers = new();

    // Kept for any existing caller that only needs the Transform (e.g. hand-written code
    // that predates the animatorTrigger addition) - prefer TryGet below for new code so
    // the paired pose trigger isn't silently dropped.
    public Transform Get(string id) => Find(id)?.transform;

    public bool TryGet(string id, out Transform transform, out string animatorTrigger)
    {
        Marker marker = Find(id);
        transform = marker?.transform;
        animatorTrigger = marker?.animatorTrigger;
        return marker != null;
    }

    private Marker Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || markers == null) return null;

        foreach (var marker in markers)
        {
            if (marker != null && string.Equals(marker.id?.Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase))
                return marker;
        }

        return null;
    }
}
