using UnityEngine;

public class LookAtPlayer : MonoBehaviour
{
    [Tooltip("Assign the Player transform directly. If left empty, this will try to find a GameObject tagged 'Player' at startup.")]
    public Transform player;

    public float rotationSpeed = 5f;

    [Tooltip("Re-assert the starting position every frame, so nothing else (Animator root motion, etc.) can drag this object out of place.")]
    public bool lockPosition = true;

    [Header("Only look at the player while in a matching pose")]
    [Tooltip("Assign Sofia's Animator directly. If left empty, this will try GetComponent<Animator>() " +
             "on this same GameObject at Start(). If no Animator can be found at all, the look-at " +
             "behaviour just runs unconditionally (fails OPEN, not closed) - this feature is opt-in, " +
             "not a new requirement for every use of this script.")]
    [SerializeField] private Animator animator;

    [Tooltip("Which Animator layer to read the current state's Tag from - 0 (Base Layer) unless " +
             "you specifically put standing/seated/reclined states on a different layer.")]
    [SerializeField] private int animatorLayerIndex = 0;

    [Tooltip("Sofia only turns to face the player while the CURRENT Animator state on the layer " +
             "above carries this exact Tag (set per-state in the Animator window's State Inspector, " +
             "next to Motion) - e.g. \"standing\" on standing_in_front_door, left blank on " +
             "chair_seated/reclined states. Prevents her visibly swivelling to face the trainee " +
             "while seated or unconscious, which reads as broken rather than in-character. Leave " +
             "this field blank to disable the check entirely and always look, regardless of pose.")]
    [SerializeField] private string requiredStateTag = "standing";

    Vector3 _lockedPosition;

    // Call this whenever something LEGITIMATELY teleports this object (e.g.
    // PatientScenarioController moving Sofia to a named PositionMarkerRegistry marker) -
    // otherwise lockPosition fights that move right back to wherever Start() first found it,
    // every LateUpdate() thereafter. Without this, the only way to relocate an object with
    // lockPosition on is to also disable/re-enable this script around the move, which is
    // easy to forget at every call site - this way the two systems just agree with each other.
    public void SetLockedPosition(Vector3 position) => _lockedPosition = position;

    void Start()
    {
        _lockedPosition = transform.position;

        if (player == null)
        {
            var found = GameObject.FindGameObjectWithTag("Player");
            if (found != null)
                player = found.transform;
            else
                Debug.LogWarning($"{nameof(LookAtPlayer)} on {name}: no player assigned and no GameObject tagged 'Player' found.");
        }

        if (animator == null)
            animator = GetComponent<Animator>();
    }

    void LateUpdate()
    {
        if (lockPosition)
            transform.position = _lockedPosition;

        if (player == null)
            return;

        if (!IsInAllowedLookAtPose())
            return;

        var direction = player.position - transform.position;
        direction.y = 0f; // stay upright - turn left/right only, never tilt up/down toward the player
        if (direction.sqrMagnitude < 0.0001f)
            return;

        var targetRotation = Quaternion.LookRotation(direction, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * rotationSpeed);
    }

    // Gates the LOOK-AT (rotation) behaviour only - lockPosition above is unaffected, since
    // resisting an unwanted position drag is just as relevant whether Sofia is standing or
    // seated. Reads the CURRENT state's Tag on animatorLayerIndex (set per-state in the
    // Animator window, e.g. "standing" on standing_in_front_door - see PositionMarkerRegistry's
    // matching marker ids for the full list of poses this eventually needs covering). Fails
    // OPEN (returns true, i.e. keeps looking) whenever the check can't meaningfully run at all -
    // no Animator assigned/found, or requiredStateTag left blank - so an unconfigured instance
    // behaves exactly like this script did before this feature existed.
    //
    // NOTE: during an active transition between states (e.g. standing -> chair_seated),
    // GetCurrentAnimatorStateInfo() still reports the OUTGOING state's tag until the transition
    // fully completes, not the incoming one - so Sofia keeps facing the player for the duration
    // of that transition rather than snapping to ignore them the instant it starts. Acceptable
    // for a several-frame crossfade; if a transition is ever made deliberately long AND this
    // needs to cut off the instant it begins, check GetNextAnimatorStateInfo()/IsInTransition()
    // here too rather than assuming this comment is describing a bug.
    private bool IsInAllowedLookAtPose()
    {
        if (animator == null || string.IsNullOrWhiteSpace(requiredStateTag))
            return true;

        return animator.GetCurrentAnimatorStateInfo(animatorLayerIndex).IsTag(requiredStateTag);
    }
}
