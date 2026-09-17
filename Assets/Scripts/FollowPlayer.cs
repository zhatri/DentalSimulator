using UnityEngine;

public class FollowPlayer : MonoBehaviour
{
    [Tooltip("Assign the Player transform directly. If left empty, this will try to find a GameObject tagged 'Player' at startup.")]
    public Transform player;

    [Header("Follow position (relative to the player)")]
    [Tooltip("X = sideways offset (+right/-left), Y = hover height, Z = forward/back offset")]
    public Vector3 followOffset = new Vector3(1.5f, 1.2f, -0.5f);
    public float followSmoothTime = 0.3f;

    [Header("Floating bob")]
    public float bobAmplitude = 0.15f;
    public float bobFrequency = 1.5f;

    [Header("Rotation")]
    public bool lookAtPlayer = true;
    public float rotationSpeed = 5f;

    [Header("Wall avoidance")]
    [Tooltip("Layers treated as solid obstacles (walls, furniture, doors) the bot should never float " +
             "through. Point this at whatever layer your static room geometry actually uses for the " +
             "cleanest result - \"Everything\" (the default) works too, since the Player and this bot's " +
             "own colliders are excluded in code regardless of what this mask includes.")]
    public LayerMask obstacleMask = ~0;
    [Tooltip("Roughly the bot's own visual radius - the wall check sweeps a sphere this size instead of " +
             "a bare line, so the bot's edge doesn't still poke into a wall it was only technically " +
             "\"clear\" of at its exact center point.")]
    public float avoidanceRadius = 0.25f;
    [Tooltip("Extra gap kept between the bot and whatever wall/obstacle it gets clamped against.")]
    public float wallClearance = 0.1f;

    Vector3 _velocity;

    void Start()
    {
        if (player == null)
        {
            var found = GameObject.FindGameObjectWithTag("Player");
            if (found != null)
                player = found.transform;
            else
                Debug.LogWarning($"{nameof(FollowPlayer)} on {name}: no player assigned and no GameObject tagged 'Player' found.");
        }
    }

    void LateUpdate()
    {
        if (player == null)
            return;

        var targetPosition = player.position
            + player.right * followOffset.x
            + Vector3.up * followOffset.y
            + player.forward * followOffset.z;
        targetPosition.y += Mathf.Sin(Time.time * bobFrequency) * bobAmplitude;

        // targetPosition above is a purely geometric offset from the player's own transform - it
        // has no idea whether that point actually lands inside or behind a wall. Assigning
        // straight to transform.position below is a plain kinematic move with zero collision
        // awareness of its own (Unity only resolves collisions for Rigidbody/CharacterController-
        // driven movement, and this script uses neither), so nothing was ever stopping the bot
        // from floating straight through a wall the instant the trainee stood close enough to one
        // that the offset point landed on its far side. Clamp the target back to the nearest clear
        // point along the same line before it's ever assigned.
        targetPosition = ClampAgainstObstacles(player.position, targetPosition);

        transform.position = Vector3.SmoothDamp(transform.position, targetPosition, ref _velocity, followSmoothTime);

        if (lookAtPlayer)
        {
            var direction = player.position - transform.position;
            if (direction.sqrMagnitude > 0.0001f)
            {
                var targetRotation = Quaternion.LookRotation(direction, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * rotationSpeed);
            }
        }
    }

    // Sweeps a sphere (roughly the bot's own size) from the player out toward the desired follow
    // point; if something solid is in the way, pulls the target back to just short of it instead
    // of letting the bot's destination sit inside/behind that geometry. Deliberately casts from
    // the PLAYER's position, not the bot's own current position - the player is always guaranteed
    // to be standing in open space, so this is a stable, always-valid cast origin, whereas the
    // bot's current position could itself already be mid-recovery from a previous clamp.
    Vector3 ClampAgainstObstacles(Vector3 from, Vector3 desired)
    {
        Vector3 offset = desired - from;
        float distance = offset.magnitude;
        if (distance < 0.0001f) return desired;

        Vector3 direction = offset / distance;

        // SphereCastAll, not SphereCast: starting the sweep exactly at the player's position means
        // a plain SphereCast would immediately report the PLAYER'S OWN collider as a hit at
        // ~0 distance (a sphere overlapping a collider at the start of its sweep counts as an
        // instant hit), collapsing the bot onto the player instead of clamping against an actual
        // wall - a much worse bug than the one this is fixing. Gathering every hit and explicitly
        // skipping the player's own hierarchy (and this bot's own, in case it has a collider on a
        // layer included in obstacleMask) sidesteps that regardless of how obstacleMask ends up
        // configured in the Inspector.
        RaycastHit[] hits = Physics.SphereCastAll(from, avoidanceRadius, direction, distance, obstacleMask, QueryTriggerInteraction.Ignore);

        float closestDistance = distance;
        bool foundObstacle = false;

        foreach (var hit in hits)
        {
            if (hit.collider.transform == player || hit.collider.transform.IsChildOf(player)) continue;
            if (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform)) continue;

            if (hit.distance < closestDistance)
            {
                closestDistance = hit.distance;
                foundObstacle = true;
            }
        }

        if (!foundObstacle) return desired;

        float clampedDistance = Mathf.Max(closestDistance - wallClearance, 0f);
        return from + direction * clampedDistance;
    }
}