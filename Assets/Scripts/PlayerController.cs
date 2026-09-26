using UnityEngine;
using UnityEngine.InputSystem;

// Shared locomotion for BOTH builds (Quest headset in the lab, keyboard+mouse at home) -
// project doc section 18's "single virtual clinic, minimal divergence between versions"
// requirement. This script only ever reads the player's CURRENT facing direction from
// viewTransform and moves relative to it; it never owns rotation for movement purposes.
//
// Rotation itself is handled completely differently per platform, and deliberately isn't
// this script's job on the VR side at all:
//   - VR (Quest): the classroom's swivel chairs let the trainee physically turn a full
//     360 degrees, so head/body tracking alone drives ALL view rotation (yaw AND pitch) -
//     the XR camera's transform is updated by the headset's own tracking (a
//     TrackedPoseDriver under the XR Origin), completely outside this script and outside
//     the Input Actions system. Do NOT wire a "Turn" action to anything on the XR control
//     scheme - there isn't one, on purpose. This is the safest possible view-rotation
//     scheme for VR comfort, since real head movement matched to the real vestibular
//     signal has no mismatch to cause sickness in the first place (unlike ANY
//     joystick-driven rotation, yaw included).
//   - Desktop: there's no head tracking to fall back on, so mouse movement drives both
//     yaw and pitch the normal FPS way - this script DOES own rotation here, via
//     ApplyMouseLook(), gated by enableMouseLook.
//
// Movement is head-relative on both platforms: pushing the stick/W forward moves the
// trainee in whatever direction they're currently facing (the VR headset's facing, or
// wherever the mouse has aimed the desktop camera) - never relative to some other fixed
// "player forward," so the WASD-relative-to-mouse-look convention on desktop and the
// stick-relative-to-head-facing convention in VR behave identically from the trainee's
// point of view, just driven by different hardware.
[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    [Header("Input (assign from the shared Input Actions asset)")]
    [Tooltip("Vector2 action bound to WASD on the Desktop control scheme and the " +
             "thumbstick on the XR control scheme. Shared and identical in behaviour " +
             "on both platforms.")]
    [SerializeField] private InputActionReference moveAction;

    [Tooltip("Vector2 (mouse delta) action - only bind this under the Desktop control " +
             "scheme. Leave it unbound (or simply don't drag an action in) for the XR " +
             "scheme; enableMouseLook below should also be off for the VR build so this " +
             "script never touches rotation there.")]
    [SerializeField] private InputActionReference lookAction;

    [Header("References")]
    [Tooltip("The Transform representing the player's current view direction: the " +
             "head-tracked XR camera in the VR build (child of the XR Origin, driven by " +
             "a TrackedPoseDriver), or the desktop free-look camera in the desktop build. " +
             "Movement direction is always derived from this, never from this " +
             "GameObject's own rotation - which matters especially in VR, where this " +
             "root object does not rotate with the trainee's head at all.")]
    [SerializeField] private Transform viewTransform;

    [Header("Movement")]
    [Tooltip("Deliberately modest - a fast move speed is its own comfort/vection risk, " +
             "independent of anything to do with rotation.")]
    [SerializeField] private float moveSpeed = 2.0f;
    [SerializeField] private float gravity = -9.81f;

    [Header("Desktop mouse-look only - leave OFF on the VR build")]
    [Tooltip("Turn this ON for the desktop build's player prefab/variant, OFF for the " +
             "VR build's - rotation in VR comes entirely from head tracking and must " +
             "never be touched by this script.")]
    [SerializeField] private bool enableMouseLook = false;
    [SerializeField] private float mouseSensitivity = 0.1f;
    [SerializeField] private float minPitch = -80f;
    [SerializeField] private float maxPitch = 80f;

    private CharacterController characterController;
    private float verticalVelocity;
    private float currentPitch;
    private float currentYaw;

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();

        if (viewTransform == null)
            Debug.LogError("[PlayerController] No viewTransform assigned - movement direction cannot be determined.");
        else
            // Seed both from whatever the camera is already facing at scene start, so the
            // desktop player doesn't snap to a different facing the instant the mouse first
            // moves.
            currentYaw = viewTransform.localEulerAngles.y;
    }

    private void OnEnable()
    {
        moveAction?.action.Enable();
        if (enableMouseLook) lookAction?.action.Enable();
    }

    private void OnDisable()
    {
        moveAction?.action.Disable();
        lookAction?.action.Disable();
    }

    private void Update()
    {
        if (SessionTelemetry.Instance != null && SessionTelemetry.Instance.IsDebriefVisible) return;
        if (viewTransform == null) return;

        if (enableMouseLook)
        {
            UpdateCursorLock();
            ApplyMouseLook();
        }
        ApplyMovement();
    }

    // Desktop only. Standard "click into the game to capture the mouse, Escape to give it
    // back" pattern - without this, ApplyMouseLook() below would spin the camera the
    // instant the pointer twitches even while the trainee is trying to click a menu, alt-tab
    // out, or use another window, and the OS cursor would stay visible on top of the game.
    // Locking hides the cursor and confines/re-centers it every frame so mouse deltas keep
    // arriving cleanly instead of stalling at the edge of the screen; Escape is the
    // universal, discoverable way back out, matching every other PC game.
    private void UpdateCursorLock()
    {
        if (Cursor.lockState != CursorLockMode.Locked
            && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else if (Cursor.lockState == CursorLockMode.Locked
            && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    // Desktop only. Both yaw and pitch are written straight onto viewTransform's own
    // local rotation - deliberately NOT via transform.Rotate() on this GameObject. Doing
    // it that way only works if viewTransform happens to be a child of this GameObject in
    // the scene hierarchy (parent yaw only shows up on the camera if the camera is
    // parented under it); writing both axes directly onto viewTransform instead makes
    // this correct regardless of hierarchy, which is what was actually causing "pitch
    // works, yaw doesn't" - the camera wasn't a child of the player root, so rotating the
    // root had no visible effect while the direct write to viewTransform's pitch still did.
    // This method should never run on the VR build (enableMouseLook stays false there) -
    // the headset's own tracking rotates viewTransform instead, with nothing here
    // touching it.
    private void ApplyMouseLook()
    {
        Vector2 lookDelta = lookAction != null ? lookAction.action.ReadValue<Vector2>() : Vector2.zero;

        currentYaw += lookDelta.x * mouseSensitivity;
        currentPitch = Mathf.Clamp(currentPitch - lookDelta.y * mouseSensitivity, minPitch, maxPitch);
        viewTransform.localEulerAngles = new Vector3(currentPitch, currentYaw, 0f);
    }

    // Shared by both builds unchanged. Reads the current facing direction from
    // viewTransform (head-tracked in VR, mouse-look camera on desktop) and moves the
    // CharacterController relative to THAT - never relative to this GameObject's own
    // rotation, since on the VR build this GameObject doesn't rotate with the trainee's
    // head at all (only viewTransform, via tracking, does).
    private void ApplyMovement()
    {
        Vector2 moveInput = moveAction != null ? moveAction.action.ReadValue<Vector2>() : Vector2.zero;

        Vector3 forward = viewTransform.forward;
        Vector3 right = viewTransform.right;
        forward.y = 0f;
        right.y = 0f;
        forward.Normalize();
        right.Normalize();

        Vector3 moveDirection = (forward * moveInput.y + right * moveInput.x) * moveSpeed;

        if (characterController.isGrounded && verticalVelocity < 0f) verticalVelocity = -1f;
        verticalVelocity += gravity * Time.deltaTime;
        moveDirection.y = verticalVelocity;

        characterController.Move(moveDirection * Time.deltaTime);
    }
}
