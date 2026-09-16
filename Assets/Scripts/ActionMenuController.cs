using UnityEngine;
using UnityEngine.InputSystem;

// Drives the right-click action-menu flow: hover an object carrying InteractableActions,
// right-click to pop up a screen-space list of its actions (ActionMenuUI), left-click one
// to trigger it. This is the desktop-first implementation the team asked for; VR support
// ("right thumb button click on XR") is a deliberate, scoped-out follow-up - see the note
// at the bottom of this comment before wiring this up for a Quest build.
//
// One instance per scene, like PatientScenarioController/SessionManager. Owns:
//   - hover detection (a camera raycast against InteractableActions colliders, same pattern
//     SofiaPersona/VirtualAssistantPersona already use for their own hover-to-talk)
//   - opening/closing the menu (right-click to open, a menu selection / clicking outside
//     the menu / Escape / the menu's own Cancel button to close)
//   - suspending "world interaction" while the menu is open: the cursor unlocks so the
//     trainee can actually move it onto a menu button, and PlayerController is disabled for
//     the same reason CinematicController already disables it during a cutscene (no walking
//     around or looking while picking from a menu) - both are restored the instant the menu
//     closes, however it closed.
//
// Deliberately does NOT do the raycast-for-hover AND the "is a menu button under the cursor"
// check in the same pass - once a menu is open, this class stops doing its own hover/right-
// click handling entirely (see Update()) and leaves button clicks to Unity's normal UI event
// system (EventSystem + Button.onClick), which is the well-supported way to handle on-screen
// UI clicks rather than hand-rolling a second raycast layer on top of the world-space one.
//
// VR FOLLOW-UP (not built here): the hover raycast below is driven by the desktop mouse
// (Camera.ScreenPointToRay against Mouse.current.position). On Quest this needs a
// controller-ray source instead (most naturally XR Interaction Toolkit's XRRayInteractor,
// which none of this project's existing scripts use yet), an "open menu" input bound to a
// controller button (the shared InputActionAsset from section 18 of the project doc is the
// right place for that binding), and a TrackedDeviceGraphicRaycaster on the Canvas so the
// same ray can click the Button-based menu ActionMenuUI already builds. None of that is
// wired up here - flagging it explicitly so it isn't mistaken for an oversight later.
public class ActionMenuController : MonoBehaviour
{
    public static ActionMenuController Instance { get; private set; }

    // Convenience for other scripts (SofiaPersona, VirtualAssistantPersona) that just need
    // to know "is a menu currently up" to avoid starting their own hover-hold-talk while the
    // trainee is picking from this menu, without needing an Inspector-dragged reference.
    public static bool AnyMenuOpen => Instance != null && Instance.isMenuOpen;

    [Header("Hover (mirrors SofiaPersona/VirtualAssistantPersona's own hover pattern)")]
    [Tooltip("Camera used to raycast for hover detection. Leave empty to use Camera.main.")]
    [SerializeField] private Camera interactionCamera;
    [SerializeField] private LayerMask hoverLayerMask = ~0;
    [SerializeField] private float maxHoverDistance = 50f;

    [Header("Wiring")]
    [SerializeField] private ActionMenuUI menuUI;
    [Tooltip("Disabled while the menu is open (same reasoning as CinematicController disabling " +
             "this during a cutscene), re-enabled the instant it closes. Leave empty if your scene " +
             "doesn't have one yet - the menu still works, just without pausing movement/look.")]
    [SerializeField] private PlayerController playerController;

    [Tooltip("The on-screen aiming crosshair/reticle GameObject, if your scene has one - hidden " +
             "while the action menu is open (the trainee is picking from a menu with a free " +
             "cursor, not aiming at anything, so the reticle sitting in the middle of the menu is " +
             "just visual clutter) and shown again the instant it closes. Leave empty if your scene " +
             "doesn't use a crosshair.")]
    [SerializeField] private GameObject crosshair;

    private InteractableActions hoveredTarget;
    private InteractableActions currentTarget;
    private bool isMenuOpen;

    // Exposed read-only for ReticleController - deliberately false while the menu is open
    // (hoveredTarget can hold a stale value from before the menu opened, since UpdateHover()
    // stops running the instant isMenuOpen flips true - see Update()'s early-return above -
    // so this guards against reporting a hover that's no longer being actively checked).
    public bool IsHoveringInteractable => !isMenuOpen && hoveredTarget != null;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[ActionMenuController] Another instance already exists in this scene - destroying this duplicate.");
            Destroy(gameObject);
            return;
        }
        Instance = this;

        if (menuUI == null)
        {
            Debug.LogError("[ActionMenuController] No ActionMenuUI assigned - the menu can never be shown.");
            enabled = false;
            return;
        }

        menuUI.OnDismissed += CloseMenu;
    }

    private void OnDestroy()
    {
        if (menuUI != null) menuUI.OnDismissed -= CloseMenu;
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        // While the Welcome/Scenario Selection calibration UI is up, this class does nothing
        // at all - no hover detection, no right-click menu. Without this, a right-click landing
        // on the calibration UI's visible screen position would still fire a Physics.Raycast
        // straight through it into the 3D scene and could pop an action menu for whatever prop
        // happens to sit behind that UI, since raycasting here has no built-in awareness of any
        // UI Canvas covering the screen (same underlying issue SofiaPersona/VirtualAssistantPersona
        // guard against with the identical check in their own Update()).
        if (WelcomeScreenController.IsCalibrationActive) return;

        if (isMenuOpen)
        {
            // While open, this class does nothing but watch for Escape - button clicks are
            // handled by Unity's UI event system, and click-away-to-dismiss is handled by
            // ActionMenuUI's backdrop button (see OnBackdropClicked above). Deliberately NOT
            // re-running hover/right-click detection here, so right-clicking a second object
            // while a menu is already open does nothing until the current one is closed -
            // simpler and more predictable than silently swapping targets mid-menu.
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                CloseMenu();

            // TEMP diagnostic - safe to delete once "menu renders but its buttons don't respond
            // to clicks" is confirmed fixed. EventSystem/GraphicRaycaster/cursor-lock are already
            // ruled out (see OpenMenu()'s own diagnostic, confirmed clean). This asks Unity's OWN
            // EventSystem what it actually hits at the click point, in front-to-back order, which
            // reveals every remaining candidate at once rather than guessing between them: an
            // invisible raycast-blocking Graphic sitting in front of the button (e.g. a cutscene
            // overlay left active from an earlier stage - CinematicController only hides
            // overlayRoot in Finish(), which never runs if an earlier video got stuck mid-
            // playback), a CanvasGroup somewhere disabling raycasts/interaction, the spawned
            // Button's own Image having Raycast Target unintentionally off, or Interactable being
            // false on the template it was cloned from.
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                LogRaycastHitsAtPointer();

            return;
        }

        UpdateHover();

        if (Mouse.current == null) return;

        if (Mouse.current.rightButton.wasPressedThisFrame)
        {
            if (hoveredTarget == null)
            {
                // Previously silent - a right-click that "does nothing" gave zero feedback
                // about WHY (ray hit nothing at all? hit something with no InteractableActions?
                // hit a collider that doesn't match what InteractableActions.HoverCollider
                // expects?). Re-diagnosing here rather than threading extra state out of
                // UpdateHover(), since this only runs on an actual miss-click, not every frame.
                LogWhyRightClickMissed();
                return;
            }

            if (hoveredTarget.Actions == null || hoveredTarget.Actions.Count == 0)
            {
                Debug.Log($"[ActionMenuController] \"{hoveredTarget.name}\" has no actions configured - ignoring right-click.");
                return;
            }

            OpenMenu(hoveredTarget, Mouse.current.position.ReadValue());
        }
    }

    private void UpdateHover()
    {
        Camera cam = interactionCamera != null ? interactionCamera : Camera.main;
        if (cam == null || Mouse.current == null)
        {
            hoveredTarget = null;
            return;
        }

        Vector2 mousePos = Mouse.current.position.ReadValue();
        Ray ray = cam.ScreenPointToRay(mousePos);

        if (Physics.Raycast(ray, out RaycastHit hit, maxHoverDistance, hoverLayerMask))
        {
            var interactable = hit.collider.GetComponentInParent<InteractableActions>();
            // Same "hit the EXACT collider this component claims, not just something nearby
            // on the same object" check SofiaPersona/VirtualAssistantPersona already use.
            hoveredTarget = (interactable != null && interactable.HoverCollider == hit.collider)
                ? interactable
                : null;
        }
        else
        {
            hoveredTarget = null;
        }
    }

    // See the TEMP diagnostic comment in Update() for why this exists. Asks Unity's own
    // EventSystem what it would actually hit at the current pointer position, in front-to-back
    // (topmost first) order - the ground truth for "why didn't my click register," since
    // whatever comes back IS what the real click resolution used, no guessing required.
    private void LogRaycastHitsAtPointer()
    {
        if (UnityEngine.EventSystems.EventSystem.current == null) return; // already reported by OpenMenu()'s diagnostic

        var pointerData = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
        {
            position = Mouse.current.position.ReadValue()
        };
        var results = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
        UnityEngine.EventSystems.EventSystem.current.RaycastAll(pointerData, results);

        if (results.Count == 0)
        {
            Debug.LogWarning("[ActionMenuController] Click while menu open hit NOTHING via EventSystem.RaycastAll() at " +
                               $"{pointerData.position} - the click isn't reaching any UI Graphic at all at this screen " +
                               "position. Double check you're actually clicking within the visible button's bounds, and " +
                               "that the action menu's Canvas Render Mode/Camera is set up correctly (see the earlier " +
                               "top-right-corner positioning bug for what a mismatch there looks like).");
            return;
        }

        var names = results.ConvertAll(r => $"\"{r.gameObject.name}\"");
        Debug.Log($"[ActionMenuController] Click while menu open hit {results.Count} UI object(s) via " +
                   $"EventSystem.RaycastAll(), TOPMOST FIRST: {string.Join(" -> ", names)}. If the action button you " +
                   "meant to click isn't FIRST in this list, whatever IS first is silently absorbing the click before it " +
                   "ever reaches the button - track down that object and either uncheck its Image's Raycast Target or " +
                   "give the menu a higher sibling/sorting order than it. If the button doesn't appear in this list AT " +
                   "ALL, check its own Image component's Raycast Target checkbox, its Button component's Interactable " +
                   "checkbox, and any CanvasGroup on its parents (Interactable/Blocks Raycasts unchecked there disables " +
                   "everything underneath it, invisibly).");
    }

    // Explains a right-click that opened nothing, by re-running the exact same raycast
    // UpdateHover() just did and reporting which of the possible mismatch reasons applies -
    // see the tooltip trail this points back to for what to actually change in each case.
    private void LogWhyRightClickMissed()
    {
        Camera cam = interactionCamera != null ? interactionCamera : Camera.main;
        if (cam == null)
        {
            Debug.Log("[ActionMenuController] Right-click did nothing - no camera available (Interaction Camera is empty and Camera.main found none).");
            return;
        }

        Ray ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());

        if (!Physics.Raycast(ray, out RaycastHit hit, maxHoverDistance, hoverLayerMask))
        {
            Debug.Log($"[ActionMenuController] Right-click hit nothing within {maxHoverDistance}m under the cursor - " +
                       "either nothing is there, it's further away than Max Hover Distance, or its Layer isn't included " +
                       "in this component's Hover Layer Mask (check both the object's Layer and that field).");
            return;
        }

        var interactable = hit.collider.GetComponentInParent<InteractableActions>();
        if (interactable == null)
        {
            Debug.Log($"[ActionMenuController] Right-click hit \"{hit.collider.name}\", but neither it nor any parent " +
                       "has an InteractableActions component - wrong object, or InteractableActions needs to be added " +
                       "higher up this object's hierarchy.");
            return;
        }

        if (interactable.HoverCollider != hit.collider)
        {
            Debug.Log($"[ActionMenuController] Right-click hit \"{hit.collider.name}\" (part of \"{interactable.name}\"), " +
                       $"but that object's InteractableActions.HoverCollider actually points at a DIFFERENT collider " +
                       $"(\"{interactable.HoverCollider?.name ?? "none"}\") - likely a separate, closer collider (e.g. an " +
                       "item sitting on/in front of this prop) is intercepting the raycast before it reaches the collider " +
                       "InteractableActions actually claims. Either aim at the exact collider Hover Collider references, " +
                       "widen/reposition that collider to cover where trainees will naturally click, or leave Hover " +
                       "Collider empty and remove the competing collider so this object's own collider wins.");
            return;
        }

        // If we get here, hoveredTarget SHOULD have been set - most likely this fired on the
        // very frame Actions.Count == 0 was already logged above, or a race between the two
        // raycasts (moving mouse between frames). Not expected in normal use.
        Debug.Log($"[ActionMenuController] Right-click hit \"{interactable.name}\" correctly on re-check - " +
                   "if the menu still didn't open, this may be a one-frame timing fluke; try again.");
    }

    private void OpenMenu(InteractableActions target, Vector2 screenPosition)
    {
        currentTarget = target;
        isMenuOpen = true;

        SetWorldInteractionSuspended(true);

        menuUI.Show(target.Actions, screenPosition, OnActionSelected);

        // TEMP diagnostic - safe to delete once "menu visible but buttons don't respond to
        // clicks" is confirmed fixed. This exact ActionMenuController/ActionMenuUI code already
        // works for other props (e.g. the dental chair), so a click failing here almost
        // certainly isn't a logic bug in this class - it's one of a handful of well-known Unity
        // UI gotchas that all produce the identical symptom (a perfectly rendered Button that
        // silently eats every click): no EventSystem in the CURRENT scene at all (most likely if
        // this prop lives in a different scene from the one the chair was tested in and that
        // scene never got its own EventSystem), no GraphicRaycaster on the Canvas the menu is
        // parented under, or the cursor still being locked/hidden (so clicks land wherever the
        // OS cursor actually is, which is NOT where the visible menu is, since a locked cursor
        // is invisible and its real position is whatever it was before locking - easy to miss
        // since the symptom looks identical to "the button just doesn't work"). Checking all
        // three here at once, right when the menu opens, rather than guessing from a screenshot.
        if (UnityEngine.EventSystems.EventSystem.current == null)
        {
            Debug.LogError("[ActionMenuController] No EventSystem exists in this scene (or the active one is disabled) - " +
                             "NO UI Button anywhere can ever receive a click without one. Add one via GameObject > UI > " +
                             "Event System if this scene doesn't already have one (each scene needs its own, unless a " +
                             "persistent one carries over via DontDestroyOnLoad from a bootstrap scene).");
        }

        var raycaster = FindAnyObjectByType<UnityEngine.UI.GraphicRaycaster>();
        if (raycaster == null)
        {
            Debug.LogError("[ActionMenuController] No GraphicRaycaster found on any Canvas in this scene - UI clicks cannot " +
                             "be detected at all without one. Check the Canvas the action menu is parented under has a " +
                             "Graphic Raycaster component (Canvas usually adds one automatically, but it can be removed by " +
                             "accident, or missing on a Canvas created/duplicated a non-standard way).");
        }
        else if (!raycaster.enabled || !raycaster.gameObject.activeInHierarchy)
        {
            Debug.LogError($"[ActionMenuController] Found a GraphicRaycaster on \"{raycaster.name}\" but it's disabled or " +
                             "its GameObject is inactive - re-enable it, UI clicks won't register while it's off.");
        }

        Debug.Log($"[ActionMenuController] Menu opened - Cursor.lockState={Cursor.lockState}, Cursor.visible={Cursor.visible}. " +
                   "If lockState isn't None here, clicks will land wherever the OS cursor actually is (which you can't see, " +
                   "since it's still locked/hidden), NOT on the visible menu - that alone would explain \"can't click it\" " +
                   "even though everything else about the menu is working correctly.");
    }

    private void OnActionSelected(string actionId)
    {
        Debug.Log($"[ActionMenuController] Trainee chose \"{actionId}\" on \"{currentTarget?.name}\".");
        currentTarget?.TriggerAction(actionId);
        CloseMenu();
    }

    private void CloseMenu()
    {
        menuUI.Hide();
        SetWorldInteractionSuspended(false);
        isMenuOpen = false;
        currentTarget = null;
    }

    // Unlocks/re-locks the cursor and disables/re-enables PlayerController while the menu is
    // up, so the trainee can move the literal mouse cursor onto a menu button without also
    // spinning the camera or walking around underneath the menu - mirrors exactly what
    // CinematicController already does to PlayerController during a cutscene, just for a
    // different reason (picking from a menu instead of watching a video).
    private void SetWorldInteractionSuspended(bool suspended)
    {
        if (playerController != null) playerController.enabled = !suspended;
        if (crosshair != null) crosshair.SetActive(!suspended);

        Cursor.lockState = suspended ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = suspended;
    }
}
