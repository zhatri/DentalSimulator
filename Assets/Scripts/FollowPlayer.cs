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
}
