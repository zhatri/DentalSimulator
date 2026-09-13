using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The actual on-screen action-list popup. Pure view - ActionMenuController owns all the
// hover/right-click/open-close DECISION logic and just tells this "show these options at
// this screen position" / "hide". Screen-space context-menu positioning (per the team's
// choice): the list appears right where the trainee right-clicked, like a normal desktop
// app's right-click menu, rather than anchored to the object in world space.
public class ActionMenuUI : MonoBehaviour
{
    [Header("Canvas (Screen Space - Overlay, or Screen Space - Camera with Canvas Camera set below)")]
    [Tooltip("The Canvas's own root RectTransform. Positioning actually measures against " +
             "panelRoot's real parent (whatever that is) - this is only used as a fallback if " +
             "panelRoot somehow isn't parented under a RectTransform at all, which shouldn't " +
             "normally happen. Still worth assigning.")]
    [SerializeField] private RectTransform canvasRect;
    [Tooltip("Only needed if the Canvas's Render Mode is Screen Space - Camera. Leave empty for " +
             "Screen Space - Overlay (the simplest, most common setup for a 2D menu like this).")]
    [SerializeField] private Camera canvasCamera;

    [Header("Menu panel")]
    [Tooltip("The visible popup itself - should have a VerticalLayoutGroup and (recommended) a " +
             "ContentSizeFitter set to resize to its buttons' combined height, so this class " +
             "doesn't need to compute sizing by hand. Pivot should be top-left (0,1) so the menu " +
             "grows down-and-right from the click point.")]
    [SerializeField] private RectTransform panelRoot;

    [Tooltip("One inactive Button (with a TMP_Text child for the label) living under panelRoot's " +
             "content parent - cloned once per action when the menu opens. Keep it SetActive(false) " +
             "in the Editor; this class re-forces that at Awake as a safety net.")]
    [SerializeField] private Button buttonTemplate;

    [Tooltip("Full-screen, invisible (or faint) Image+Button placed BEHIND panelRoot in the " +
             "hierarchy (earlier sibling, so a click on the panel itself doesn't also reach this). " +
             "Clicking anywhere outside the menu hits this instead and closes the menu - the " +
             "standard \"click-away to dismiss\" pattern, complementary to (not redundant with) " +
             "cancelButton below: this catches trainees who just click elsewhere without reading " +
             "the list, cancelButton is the explicit, discoverable option IN the list. Optional - " +
             "leave empty to remove click-away-to-dismiss entirely and require Cancel or Escape.")]
    [SerializeField] private Button backdropButton;

    [Tooltip("A REAL, permanent Button (not cloned per-action like buttonTemplate) that closes the " +
             "menu without triggering anything - the explicit \"Cancel\" option the trainee can " +
             "click instead of clicking away. Put it under the same content parent as " +
             "buttonTemplate so it lays out as part of the same list (Vertical Layout Group); it " +
             "doesn't matter where you place it in the Hierarchy - Show() always moves it to the " +
             "end of the list at runtime, so it's guaranteed to render after every real action. " +
             "Leave its own onClick empty in the Inspector, same as backdropButton - wired in code. " +
             "Optional (a null value here just means trainees can only dismiss via click-away or " +
             "Escape), but recommended for a real menu.")]
    [SerializeField] private Button cancelButton;

    [Tooltip("Minimum gap (pixels) kept between the panel and the screen edges when clamping its " +
             "position, so it never renders partly off-screen when opened near an edge.")]
    [SerializeField] private float screenEdgeMargin = 8f;

    // Fired when the menu is dismissed WITHOUT choosing an action - either the backdrop was
    // clicked or the dedicated Cancel button was. ActionMenuController subscribes to this to
    // restore the cursor/player-controller state, same as it does after an actual selection.
    // (Named OnDismissed, not OnBackdropClicked, now that there are two ways to trigger it.)
    public event Action OnDismissed;

    private readonly List<GameObject> spawnedButtons = new();

    // Cached at Awake so PositionAt() doesn't call GetComponentInParent every click.
    private Canvas rootCanvas;

    private void Awake()
    {
        buttonTemplate.gameObject.SetActive(false);
        panelRoot.gameObject.SetActive(false);

        if (backdropButton != null)
        {
            backdropButton.gameObject.SetActive(false);
            backdropButton.onClick.AddListener(() => OnDismissed?.Invoke());
        }

        if (cancelButton != null)
        {
            // Force-active regardless of how it was left in the Editor - same safety-net
            // reasoning as the SetActive(false) calls above for the other pieces, just
            // inverted, since this one really should always be visible whenever panelRoot is.
            cancelButton.gameObject.SetActive(true);
            cancelButton.onClick.AddListener(() => OnDismissed?.Invoke());
        }
        else
        {
            Debug.LogWarning("[ActionMenuUI] No Cancel Button assigned - trainees can still dismiss the " +
                              "menu by clicking away or pressing Escape, but there's no explicit \"Cancel\" " +
                              "option in the list itself. Assign one in the Inspector to add it.");
        }

        rootCanvas = panelRoot.GetComponentInParent<Canvas>();
        if (rootCanvas == null)
        {
            Debug.LogWarning("[ActionMenuUI] panelRoot isn't under a Canvas at all - positioning will fall back to canvasRect and may be wrong.");
        }
        else if (rootCanvas.renderMode == RenderMode.WorldSpace)
        {
            Debug.LogWarning("[ActionMenuUI] Canvas render mode is World Space - this class positions the menu using SCREEN coordinates " +
                              "(mouse position), which doesn't make sense for a World Space canvas. Use Screen Space - Overlay or " +
                              "Screen Space - Camera instead.");
        }
        else if (rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay && canvasCamera != null)
        {
            Debug.LogWarning("[ActionMenuUI] Canvas Camera is assigned in the Inspector, but the Canvas's Render Mode is " +
                              "Screen Space - Overlay, which should always use a NULL camera for screen-to-UI conversion. A " +
                              "non-null camera here silently produces wrong (often wildly-off, edge-of-screen) positions - this " +
                              "assigned value is now ignored at runtime so positioning is correct regardless, but clear the field " +
                              "in the Inspector to avoid confusion.");
        }
        else if (rootCanvas.renderMode == RenderMode.ScreenSpaceCamera && canvasCamera == null && rootCanvas.worldCamera == null)
        {
            Debug.LogWarning("[ActionMenuUI] Canvas Render Mode is Screen Space - Camera, but neither this component's Canvas " +
                              "Camera field nor the Canvas's own Render Camera is set - screen-to-UI conversion needs a camera " +
                              "in this mode and positioning will be wrong without one.");
        }
    }

    // The camera ScreenPointToLocalPointInRectangle should actually use, derived from the
    // Canvas's own render mode rather than trusting whatever is (or isn't) sitting in the
    // canvasCamera Inspector field - see the warnings in Awake(). Screen Space - Overlay must
    // always get null; Screen Space - Camera should get the Inspector value if set, falling
    // back to the Canvas's own assigned Render Camera otherwise.
    private Camera ResolveEffectiveCamera()
    {
        if (rootCanvas == null) return canvasCamera; // best-effort fallback

        return rootCanvas.renderMode switch
        {
            RenderMode.ScreenSpaceOverlay => null,
            RenderMode.ScreenSpaceCamera => canvasCamera != null ? canvasCamera : rootCanvas.worldCamera,
            _ => canvasCamera,
        };
    }

    public void Show(IReadOnlyList<InteractableActions.ActionOption> actions, Vector2 screenPosition,
                      Action<string> onActionSelected)
    {
        ClearSpawnedButtons();

        int skippedCount = 0;

        foreach (var option in actions)
        {
            // Silently skipping a bad entry here used to be the likely cause of "I configured
            // 2 actions but only 1 shows up" - a blank Action Id (e.g. an entry added in the
            // Inspector's list but never actually filled in) would vanish with no trace. Now
            // logged explicitly, and counted below, so a config mistake is visible in the
            // Console instead of looking like a bug in this class.
            if (option == null || string.IsNullOrWhiteSpace(option.actionId))
            {
                skippedCount++;
                Debug.LogWarning($"[ActionMenuUI] Skipping an action entry with a blank/missing Action Id " +
                                   $"(label was \"{option?.label}\") - check the InteractableActions list on " +
                                   "the object you right-clicked for an empty entry.");
                continue;
            }

            GameObject clone = Instantiate(buttonTemplate.gameObject, buttonTemplate.transform.parent);
            clone.SetActive(true);

            TMP_Text label = clone.GetComponentInChildren<TMP_Text>();
            if (label != null)
                label.text = string.IsNullOrWhiteSpace(option.label) ? option.actionId : option.label;

            string capturedActionId = option.actionId;
            Button button = clone.GetComponent<Button>();
            button.onClick.AddListener(() => onActionSelected?.Invoke(capturedActionId));

            spawnedButtons.Add(clone);
        }

        // Cancel is a real, permanent button (not cloned), but it still needs to render AFTER
        // every action button regardless of where it happens to sit in the Hierarchy in the
        // Editor - Instantiate() above always appends new siblings at the end of the parent,
        // so without this, Cancel would end up ABOVE the actions instead of below them
        // whenever it's placed earlier in the Hierarchy than buttonTemplate.
        if (cancelButton != null) cancelButton.transform.SetAsLastSibling();

        Debug.Log($"[ActionMenuUI] Show() received {actions.Count} action entr{(actions.Count == 1 ? "y" : "ies")}, " +
                  $"skipped {skippedCount}, spawned {spawnedButtons.Count} button(s). If this says 2 spawned but you " +
                  "still only SEE one, it's a Vertical Layout Group / button-height layout issue, not a missing action.");

        if (backdropButton != null) backdropButton.gameObject.SetActive(true);
        panelRoot.gameObject.SetActive(true);

        // Buttons were just added, so the VerticalLayoutGroup/ContentSizeFitter haven't
        // resized panelRoot yet this frame - force it now so the clamp below measures the
        // panel's REAL size, not last frame's (e.g. empty/stale) size.
        LayoutRebuilder.ForceRebuildLayoutImmediate(panelRoot);

        PositionAt(screenPosition);
    }

    public void Hide()
    {
        panelRoot.gameObject.SetActive(false);
        if (backdropButton != null) backdropButton.gameObject.SetActive(false);
        ClearSpawnedButtons();
    }

    private void ClearSpawnedButtons()
    {
        foreach (var go in spawnedButtons)
            if (go != null) Destroy(go);
        spawnedButtons.Clear();
    }

    // PREVIOUS BUG, fixed here: this used to assign the raw ScreenPointToLocalPointInRectangle
    // result straight to panelRoot.anchoredPosition, which only works if panelRoot's anchors
    // are centered (0.5, 0.5). ScreenPointToLocalPointInRectangle returns a point in the
    // CANVAS's own local space (origin at the canvas's pivot - the screen centre, for a
    // normal full-stretch Canvas). anchoredPosition, though, is measured from wherever
    // panelRoot's OWN anchor sits within its parent - which is the canvas's TOP-LEFT CORNER
    // if panelRoot uses a top-left anchor preset (anchorMin = anchorMax = (0,1)), not the
    // centre. Setting pivot to (0,1) - as this class's own Inspector guidance asks for, so
    // the menu grows down-and-right from the click point - does NOT by itself change the
    // anchor; picking a "top-left" anchor preset in the Rect Transform inspector changes
    // anchorMin/anchorMax too, and THAT combination is exactly what pins the menu to the
    // screen's top-left corner regardless of where you actually right-clicked - the anchor
    // reference point and the position being assigned were in two different coordinate
    // origins. Recomputed below to work correctly for ANY anchor preset panelRoot happens to
    // have (as long as it's a point anchor, i.e. anchorMin == anchorMax - true for any
    // non-stretched popup panel), by explicitly finding where that anchor point actually
    // falls in the parent's local space and subtracting it out, rather than assuming it's
    // the parent's centre.
    private void PositionAt(Vector2 screenPosition)
    {
        RectTransform parentRect = panelRoot.parent as RectTransform;
        if (parentRect == null) parentRect = canvasRect; // best-effort fallback, shouldn't normally be needed

        // Deliberately NOT using the canvasCamera field directly - see ResolveEffectiveCamera().
        // Screen Space - Overlay must get a null camera here or the conversion below silently
        // produces very wrong (often extreme, edge-of-screen) coordinates.
        Camera effectiveCamera = ResolveEffectiveCamera();

        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            parentRect, screenPosition, effectiveCamera, out Vector2 localPointInParent);

        if (panelRoot.anchorMin != panelRoot.anchorMax)
        {
            Debug.LogWarning("[ActionMenuUI] panelRoot has a STRETCHED anchor (anchorMin != anchorMax) - " +
                              "this positioning logic assumes a point anchor (anchorMin == anchorMax), like " +
                              "any normal floating popup panel would use. Positioning may be wrong; give " +
                              "panelRoot a non-stretched anchor preset (any corner or centre - pivot is what " +
                              "controls growth direction, not the anchor choice).");
        }

        Rect parentLocalRect = parentRect.rect;
        Vector2 anchorPointInParent = new Vector2(
            Mathf.Lerp(parentLocalRect.xMin, parentLocalRect.xMax, panelRoot.anchorMin.x),
            Mathf.Lerp(parentLocalRect.yMin, parentLocalRect.yMax, panelRoot.anchorMin.y));

        Vector2 desiredAnchoredPosition = localPointInParent - anchorPointInParent;

        // Clamp so the panel's own rect never extends past the parent's edges (minus a small
        // margin) - accounts for panelRoot's actual pivot, since pivot determines how far the
        // visible rect extends to either side of anchoredPosition (a pivot of (0,1) extends
        // entirely right-and-down from it; a centred pivot extends both ways evenly; etc.),
        // so this clamps correctly regardless of which pivot was chosen too.
        Vector2 panelSize = panelRoot.rect.size;

        float minX = parentLocalRect.xMin - anchorPointInParent.x + panelRoot.pivot.x * panelSize.x + screenEdgeMargin;
        float maxX = parentLocalRect.xMax - anchorPointInParent.x - (1f - panelRoot.pivot.x) * panelSize.x - screenEdgeMargin;
        float minY = parentLocalRect.yMin - anchorPointInParent.y + panelRoot.pivot.y * panelSize.y + screenEdgeMargin;
        float maxY = parentLocalRect.yMax - anchorPointInParent.y - (1f - panelRoot.pivot.y) * panelSize.y - screenEdgeMargin;

        // Mathf.Clamp needs min <= max - if the panel is wider/taller than its parent itself
        // (shouldn't normally happen, but cheap to guard), Min/Max below keeps the clamp
        // valid instead of throwing/behaving oddly.
        desiredAnchoredPosition.x = Mathf.Clamp(desiredAnchoredPosition.x, Mathf.Min(minX, maxX), Mathf.Max(minX, maxX));
        desiredAnchoredPosition.y = Mathf.Clamp(desiredAnchoredPosition.y, Mathf.Min(minY, maxY), Mathf.Max(minY, maxY));

        // TEMP diagnostic - keep this until the current positioning issue is confirmed fixed,
        // then feel free to delete it. If the menu is still landing in a corner, paste this
        // line back: localPointInParent should be roughly proportional to where you actually
        // clicked on screen (near (0,0) for a centre click, growing toward the edges) - if it's
        // instead some huge number (thousands+) regardless of where you click, the camera/render
        // mode mismatch below is still the problem, not the clamp/anchor math.
        Debug.Log($"[ActionMenuUI] click screenPos={screenPosition}, renderMode={(rootCanvas != null ? rootCanvas.renderMode.ToString() : "?")}, " +
                  $"effectiveCamera={(effectiveCamera != null ? effectiveCamera.name : "null")}, localPointInParent={localPointInParent}, " +
                  $"anchorMin={panelRoot.anchorMin}, pivot={panelRoot.pivot}, panelSize={panelSize}, " +
                  $"desired(pre-clamp not shown)={desiredAnchoredPosition}, clampX=[{Mathf.Min(minX, maxX)},{Mathf.Max(minX, maxX)}], clampY=[{Mathf.Min(minY, maxY)},{Mathf.Max(minY, maxY)}]");

        panelRoot.anchoredPosition = desiredAnchoredPosition;
    }
}
