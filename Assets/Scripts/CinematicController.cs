using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

// Plays a pre-rendered, letterboxed cutscene between patient stages - step 2 of the
// transition flow (trigger -> optional cutscene -> pose/position change for the new
// stage). The cutscenes themselves are plain video files made OUTSIDE Unity (e.g.
// AI-generated with Dreamina) and looked up by id through CinematicCatalog, so adding or
// swapping one is a spreadsheet edit, not a rebuild.
//
// Supports TWO forms for a catalog entry's videoUrl, decided per-play in Play() - see
// LooksLikeUrl()/ToResourcesPath():
//   1. A real URL (http://, https://, file://) - the production path this was designed
//      around: a video hosted somewhere outside Unity, played via VideoPlayer's Url source.
//   2. A local Resources-folder path/asset name (anything else) - for quick testing before
//      a video is actually hosted anywhere: drop the .mp4 under any folder literally named
//      "Resources" (e.g. Assets/Resources/Movies/sit_on_chair.mp4) and played via
//      VideoPlayer's VideoClip source instead. IMPORTANT: VideoPlayer's Url source does
//      NOT understand a raw "Assets/..." path - a plain relative string there is resolved
//      against the StreamingAssets folder, not the actual Asset path, which is exactly what
//      produced "WindowsMediaFoundation received empty file .../Assets/Resources/Movies/
//      sit_on_chair.mp4" / "Cannot read file." the first time this was tried. ToResourcesPath()
//      forgivingly accepts "Assets/Resources/Movies/sit_on_chair.mp4", "Resources/Movies/
//      sit_on_chair.mp4", or the bare "Movies/sit_on_chair" Resources.Load() actually wants,
//      and normalizes any of them to the last form.
//
// Deliberately does NOT touch the trainee's own camera/head-tracking at all - unlike a
// scripted in-scene animation "cinematic," this is just a flat video drawn on a Canvas
// that sits in front of whatever the trainee is currently looking at. In VR the trainee
// can still freely turn their head (their swivel chair, their choice) during playback and
// the letterboxed video stays centred in view the whole time, since it's on a camera-
// relative Canvas - there's no risk of the camera-cut/forced-viewpoint kind of VR
// sickness trigger, because the trainee's own view is never moved by this script.
[RequireComponent(typeof(VideoPlayer))]
public class CinematicController : MonoBehaviour
{
    [Header("Wiring")]
    [Tooltip("Where cinematicId -> video URL lookups come from.")]
    [SerializeField] private CinematicCatalog catalog;

    [Tooltip("Disabled for the duration of playback so the trainee can't move/mouse-look or talk " +
             "to Sofia mid-cutscene. Safe to toggle .enabled repeatedly - both scripts wire their " +
             "listeners in OnEnable/OnDisable specifically so this is idempotent.")]
    [SerializeField] private PlayerController playerController;
    [SerializeField] private SofiaPersona sofiaPersona;

    [Header("Overlay UI (a Canvas the trainee sees regardless of where they look, inactive by default)")]
    [Tooltip("Root of the letterbox/video overlay - SetActive(true) for the duration of playback. " +
             "Should already contain videoImage plus your black letterbox bars laid out around it " +
             "(e.g. a vertically-centred band with black Image bars above and below).")]
    [SerializeField] private GameObject overlayRoot;

    [Tooltip("The RawImage the video is drawn onto - sized/anchored to the letterboxed centre band.")]
    [SerializeField] private RawImage videoImage;

    private VideoPlayer videoPlayer;
    private RenderTexture renderTexture;
    private Action pendingOnComplete;

    private void Awake()
    {
        videoPlayer = GetComponent<VideoPlayer>();
        videoPlayer.playOnAwake = false;
        videoPlayer.isLooping = false;
        videoPlayer.renderMode = VideoRenderMode.RenderTexture;
        // videoPlayer.source is deliberately NOT set here - it's decided fresh in Play(),
        // per cutscene, since a URL entry needs VideoSource.Url and a local Resources entry
        // needs VideoSource.VideoClip (see class comment).

        if (overlayRoot != null) overlayRoot.SetActive(false);
    }

    private void OnEnable()
    {
        videoPlayer.prepareCompleted += HandlePrepared;
        videoPlayer.loopPointReached += HandleFinished;
        videoPlayer.errorReceived += HandleError;
    }

    private void OnDisable()
    {
        videoPlayer.prepareCompleted -= HandlePrepared;
        videoPlayer.loopPointReached -= HandleFinished;
        videoPlayer.errorReceived -= HandleError;
    }

    // Call this from PatientScenarioController whenever a stage's cinematicId should play
    // before that stage is applied. onComplete fires exactly once, synchronously and
    // immediately if there is nothing to play (blank id, or id not found in the catalog
    // yet - e.g. the remote catalog sheet hasn't finished loading), or asynchronously once
    // the video actually finishes/errors out - so callers never need to special-case
    // "no cutscene", they can always just route through here.
    public void Play(string cinematicId, Action onComplete)
    {
        if (string.IsNullOrWhiteSpace(cinematicId) || catalog == null || !catalog.TryGetUrl(cinematicId, out string url))
        {
            if (!string.IsNullOrWhiteSpace(cinematicId))
                Debug.LogWarning($"[CinematicController] No video found for cinematicId \"{cinematicId}\" - skipping cutscene.");
            onComplete?.Invoke();
            return;
        }

        pendingOnComplete = onComplete;

        if (playerController != null) playerController.enabled = false;
        if (sofiaPersona != null) sofiaPersona.enabled = false;
        if (overlayRoot != null) overlayRoot.SetActive(true);

        if (LooksLikeUrl(url))
        {
            videoPlayer.source = VideoSource.Url;
            videoPlayer.url = url;
            videoPlayer.Prepare();
            return;
        }

        // Not a URL - treat it as a local Resources-folder entry instead (see class comment).
        string resourcesPath = ToResourcesPath(url);
        VideoClip clip = Resources.Load<VideoClip>(resourcesPath);
        string[] candidatesInFolder = Array.Empty<string>();

        // Resources.Load is CASE-SENSITIVE, even on Windows where the underlying filesystem
        // isn't - a clip that's visibly right there in the Project window at the expected
        // path can still come back null here purely over a casing difference (or an extra
        // character Unity's Project view happened to truncate out of view). Rather than just
        // failing, fall back to a case-insensitive match among everything Resources actually
        // finds in that folder, and use it if there's exactly one - this makes the common
        // case (a name that matches except for case) work without needing a rename first.
        if (clip == null)
        {
            VideoClip fallback = FindClipCaseInsensitive(resourcesPath, out candidatesInFolder);
            if (fallback != null)
            {
                Debug.LogWarning($"[CinematicController] cinematicId \"{cinematicId}\": Resources.Load<VideoClip>(\"{resourcesPath}\") " +
                                   $"found nothing, but a case-insensitive match (\"{fallback.name}\") exists in the same folder and is " +
                                   "being used instead this time. Resources.Load is case-sensitive - worth renaming the catalog entry " +
                                   "or the asset itself to match exactly so this doesn't depend on the fallback going forward.");
                clip = fallback;
            }
        }

        if (clip == null)
        {
            string foundList = candidatesInFolder.Length > 0
                ? string.Join(", ", candidatesInFolder)
                : "(no VideoClip assets found in that Resources folder at all - double check the folder name/nesting itself, " +
                  "e.g. a typo in \"Movies\", or the file not actually being under a folder literally named \"Resources\")";

            Debug.LogError($"[CinematicController] cinematicId \"{cinematicId}\"'s catalog value \"{url}\" isn't a URL " +
                             $"(http://, https://, file://) and no VideoClip was found for Resources path \"{resourcesPath}\". " +
                             $"VideoClip(s) Unity actually finds in that folder: {foundList}. Compare that name character-by-" +
                             $"character against \"{resourcesPath}\" - a mismatch here (case, extra space, a doubled \".mp4.mp4\" " +
                             "from a rename, etc.) is the usual cause once the file is confirmed to exist in the Project window. " +
                             "Skipping cutscene.");
            Finish();
            return;
        }

        videoPlayer.source = VideoSource.VideoClip;
        videoPlayer.clip = clip;
        videoPlayer.Prepare();
    }

    // Looks for a VideoClip whose name matches the requested path's leaf name, ignoring
    // case, among everything Resources.LoadAll finds in that same folder - and reports back
    // every name it DID find there either way, so a failed lookup comes with a concrete list
    // to compare against instead of just "not found, go check the Project window yourself".
    private static VideoClip FindClipCaseInsensitive(string resourcesPath, out string[] candidatesInFolder)
    {
        int lastSlash = resourcesPath.LastIndexOf('/');
        string folder = lastSlash >= 0 ? resourcesPath.Substring(0, lastSlash) : "";
        string leafName = lastSlash >= 0 ? resourcesPath.Substring(lastSlash + 1) : resourcesPath;

        VideoClip[] clipsInFolder = Resources.LoadAll<VideoClip>(folder);
        candidatesInFolder = clipsInFolder.Select(c => c.name).ToArray();

        return clipsInFolder.FirstOrDefault(c => string.Equals(c.name, leafName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("file://", StringComparison.OrdinalIgnoreCase);

    // Forgiving conversion from whatever a tutor/dev might type for a LOCAL testing entry
    // (a full "Assets/Resources/..." path, a "Resources/..." path, or already the bare key)
    // into the exact string Resources.Load<T>() actually wants: no "Assets/"/"Resources/"
    // prefix, no file extension, forward slashes. Resources.Load is picky about this - it
    // does NOT accept a project-relative path or a file extension, which is exactly what
    // tripped this up originally (VideoPlayer's Url mode silently tried to resolve
    // "Assets/Resources/Movies/x.mp4" as a path relative to StreamingAssets instead).
    private static string ToResourcesPath(string raw)
    {
        string path = raw.Replace('\\', '/').Trim().TrimStart('/');

        int resourcesIndex = path.IndexOf("Resources/", StringComparison.OrdinalIgnoreCase);
        if (resourcesIndex >= 0)
            path = path.Substring(resourcesIndex + "Resources/".Length);

        int lastDot = path.LastIndexOf('.');
        int lastSlash = path.LastIndexOf('/');
        if (lastDot > lastSlash) // an extension exists on the final path segment - strip it
            path = path.Substring(0, lastDot);

        return path;
    }

    private void HandlePrepared(VideoPlayer vp)
    {
        // The clip's real dimensions aren't known until Prepare() completes, so the
        // RenderTexture is (re)built here rather than in Awake - this also means it's
        // correctly resized if two cutscenes in a row happen to have different resolutions.
        int width = Mathf.Max(1, (int)vp.width);
        int height = Mathf.Max(1, (int)vp.height);

        if (renderTexture == null || renderTexture.width != width || renderTexture.height != height)
        {
            if (renderTexture != null) renderTexture.Release();
            renderTexture = new RenderTexture(width, height, 0);
            vp.targetTexture = renderTexture;
            if (videoImage != null) videoImage.texture = renderTexture;
        }

        vp.Play();
    }

    private void HandleFinished(VideoPlayer vp) => Finish();

    private void HandleError(VideoPlayer vp, string message)
    {
        Debug.LogError($"[CinematicController] Video playback error ({message}) - skipping the rest of this cutscene.");
        Finish();
    }

    private void Finish()
    {
        if (overlayRoot != null) overlayRoot.SetActive(false);
        if (playerController != null) playerController.enabled = true;
        if (sofiaPersona != null) sofiaPersona.enabled = true;

        Action callback = pendingOnComplete;
        pendingOnComplete = null;
        callback?.Invoke();
    }
}
