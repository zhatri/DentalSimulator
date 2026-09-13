using System;
using UnityEngine;
using UnityEngine.UI;

// Swaps a single on-screen reticle/crosshair Image between different looks depending on
// what's currently under it - one look while hovering a TALKABLE character (Sofia or the
// Virtual Assistant bot), a different one while hovering an INTERACTABLE prop (anything
// with InteractableActions - a syringe, the dental chair, etc.), and a default/neutral look
// otherwise. Each look can be a single static Sprite, or a short sequence of Sprites played
// back on a timer to fake a simple looping animation - Unity has no built-in .gif importer,
// so an actual animated GIF needs to be exported as a sequence of frame images (or a sprite
// sheet sliced into individual Sprites) first; there's nothing further this script can do
// with a raw .gif file dropped into the project.
//
// Deliberately does NOT do its own hover raycasting - SofiaPersona, VirtualAssistantPersona,
// and ActionMenuController each already run their own hover raycast every frame for their
// own purposes (hover-to-talk, the right-click menu), and this just POLLS each one's already-
// computed result (their own IsHovering / IsHoveringInteractable properties) in LateUpdate(),
// which Unity guarantees runs after every script's own Update() this frame regardless of
// script execution order. That ordering guarantee matters here specifically: if this instead
// polled from its own Update(), or - worse - if each of those three scripts pushed a
// "set reticle to X" call out to this class from inside their OWN Update(), the actual
// displayed reticle for a given frame would depend on which script's Update() happened to
// run last (Unity does not guarantee Update() order across independent MonoBehaviours) -
// exactly the kind of script-execution-order race already flagged elsewhere in this project
// (see RemoteScenarioApiLoader's autoStartLoading/BeginLoading() fix). Reading from
// LateUpdate() instead sidesteps that entirely: by the time this runs, every hover script's
// own Update() for this frame has already finished, so there's nothing left to race against.
//
// Works alongside ActionMenuController's existing "crosshair" field, not in place of it -
// point that Inspector field at this script's own root GameObject (or a parent of it) and it
// keeps being hidden/shown around the action menu exactly as it already is; this script only
// decides WHICH sprite shows while that GameObject is active, and simply doesn't run at all
// while it's inactive (Unity doesn't call Update()/LateUpdate() on a disabled GameObject),
// so no extra "is the menu open" check is needed here on top of that.
public class ReticleController : MonoBehaviour
{
    public enum HoverKind
    {
        None,
        Talkable,
        Interactable
    }

    [Serializable]
    public class ReticleVisual
    {
        [Tooltip("Used when Animation Frames below is empty - a single, unchanging reticle image.")]
        public Sprite staticSprite;

        [Tooltip("Optional. If you have more than one frame (e.g. exported from a GIF, or a hand-" +
                 "drawn flipbook), list them here in playback order instead of filling in Static " +
                 "Sprite - this cycles through them on a loop at Frames Per Second below for a " +
                 "simple animated-reticle effect. Leave empty to just use Static Sprite as a plain, " +
                 "unanimated image.")]
        public Sprite[] animationFrames = Array.Empty<Sprite>();

        [Tooltip("Playback speed for Animation Frames above. Ignored if Animation Frames is empty.")]
        public float framesPerSecond = 12f;

        public bool IsAnimated => animationFrames != null && animationFrames.Length > 1;
    }

    [Tooltip("The UI Image whose sprite gets swapped. This is your actual on-screen reticle - " +
             "typically a small Image centered on-screen, the same one ActionMenuController's " +
             "\"Crosshair\" field points at (or a child of it).")]
    [SerializeField] private Image reticleImage;

    [Header("Looks per hover state")]
    [Tooltip("Shown when the cursor isn't over anything reticle-relevant.")]
    [SerializeField] private ReticleVisual defaultReticle;

    [Tooltip("Shown while hovering Sofia or the Virtual Assistant bot - anything that answers " +
             "yes to IsHovering below.")]
    [SerializeField] private ReticleVisual talkableReticle;

    [Tooltip("Shown while hovering an InteractableActions prop (via ActionMenuController's own " +
             "hover detection) - a syringe, the dental chair, etc.")]
    [SerializeField] private ReticleVisual interactableReticle;

    [Header("Hover sources (poll-only - see class comment for why this doesn't raycast itself)")]
    [Tooltip("Optional. Leave empty if this scene has no Sofia character yet.")]
    [SerializeField] private SofiaPersona sofiaPersona;

    [Tooltip("Optional. Leave empty if this scene has no Virtual Assistant bot yet.")]
    [SerializeField] private VirtualAssistantPersona virtualAssistantPersona;

    // Interactable-prop hover comes from ActionMenuController.Instance automatically - no
    // Inspector reference needed for it, same as InteractableActions/SofiaPersona/
    // VirtualAssistantPersona already default to it elsewhere in this project.

    private HoverKind currentKind = HoverKind.None;
    private int frameIndex;
    private float frameTimer;

    private void Start()
    {
        if (reticleImage == null)
        {
            Debug.LogError("[ReticleController] No Reticle Image assigned - this script has nothing to draw to.");
            enabled = false;
            return;
        }

        // Establish whatever the "nothing hovered" look is immediately, rather than leaving
        // the Image showing whatever sprite happened to be authored on it in the Editor until
        // the first hover-state CHANGE (which might not happen for several seconds if the
        // trainee doesn't move the mouse over anything right away).
        ApplyVisual(GetVisual(currentKind), resetAnimation: true);
    }

    private void LateUpdate()
    {
        bool talkableHover = (sofiaPersona != null && sofiaPersona.IsHovering) ||
                              (virtualAssistantPersona != null && virtualAssistantPersona.IsHovering);
        bool interactableHover = ActionMenuController.Instance != null &&
                                  ActionMenuController.Instance.IsHoveringInteractable;

        // Talkable wins if a scene somehow has both true at once (shouldn't happen under
        // normal use - a single forward ray only ever hits one collider first - but a defined
        // precedence beats an undefined one if colliders ever overlap oddly).
        HoverKind newKind = talkableHover ? HoverKind.Talkable
                           : interactableHover ? HoverKind.Interactable
                           : HoverKind.None;

        if (newKind != currentKind)
        {
            currentKind = newKind;
            ApplyVisual(GetVisual(currentKind), resetAnimation: true);
            return;
        }

        // Same hover state as last frame - just advance the animation, if the current visual
        // has more than one frame. Resetting frameIndex only happens on a STATE CHANGE above,
        // so a continuously-hovered target plays through its animation once and loops, rather
        // than restarting from frame 0 every single frame.
        AdvanceAnimation(GetVisual(currentKind));
    }

    private ReticleVisual GetVisual(HoverKind kind) => kind switch
    {
        HoverKind.Talkable => talkableReticle,
        HoverKind.Interactable => interactableReticle,
        _ => defaultReticle,
    };

    private void ApplyVisual(ReticleVisual visual, bool resetAnimation)
    {
        if (resetAnimation)
        {
            frameIndex = 0;
            frameTimer = 0f;
        }

        if (visual == null)
        {
            reticleImage.sprite = null;
            return;
        }

        reticleImage.sprite = visual.IsAnimated ? visual.animationFrames[frameIndex] : visual.staticSprite;
    }

    private void AdvanceAnimation(ReticleVisual visual)
    {
        if (visual == null || !visual.IsAnimated) return;

        float frameDuration = visual.framesPerSecond > 0f ? 1f / visual.framesPerSecond : 0f;
        if (frameDuration <= 0f) return;

        frameTimer += Time.deltaTime;
        if (frameTimer < frameDuration) return;

        frameTimer -= frameDuration;
        frameIndex = (frameIndex + 1) % visual.animationFrames.Length;
        reticleImage.sprite = visual.animationFrames[frameIndex];
    }
}
