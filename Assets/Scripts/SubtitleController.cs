using System.Collections;
using TMPro;
using UnityEngine;

// Bottom-of-screen subtitle line for every voice in the simulation: the trainee
// (STT, from SpeechToTextAgent.onTranscript), Sofia (TTS, each sentence-chunk as
// it's actually dispatched to speech), and a future Virtual Assistant / narrator
// character - the Speaker enum below has a slot reserved for it already, so wiring
// that character up later is just calling ShowVirtualAssistantLine() from wherever
// its dialogue is dispatched, no changes needed here. Deliberately dumb/stateless
// beyond an auto-hide timer - callers decide WHAT to show and WHEN; this class only
// owns formatting it with the speaker's name and clearing it again after a delay.
public class SubtitleController : MonoBehaviour
{
    // One entry per voice that can appear in the conversation. Add to this (and to
    // LabelFor() below) if another speaking character joins later.
    public enum Speaker
    {
        Trainee,
        Sofia,
        VirtualAssistant
    }

    [Header("UI references")]
    [Tooltip("The TextMeshPro text element the subtitle line is written into (a UI Text - " +
             "TextMeshPro on a Canvas anchored to the bottom of the screen).")]
    [SerializeField] private TMP_Text subtitleText;

    [Tooltip("Optional container (e.g. a background panel behind the text) to show/hide along " +
             "with the subtitle. Leave empty if subtitleText alone should just go blank when idle.")]
    [SerializeField] private GameObject subtitlePanel;

    [Header("Behaviour")]
    [Tooltip("How long a line stays on screen before it's cleared, in seconds. A new line " +
             "arriving before this elapses replaces the text and restarts the timer.")]
    [SerializeField] private float holdDuration = 4f;

    [SerializeField] private bool showSpeakerLabel = true;

    [Header("Speaker display names")]
    [SerializeField] private string traineeLabel = "You";
    [SerializeField] private string sofiaLabel = "Sofia";
    [SerializeField] private string virtualAssistantLabel = "Virtual Assistant";

    private Coroutine hideRoutine;

    private void Awake()
    {
        // Force word-wrap and non-truncating overflow at runtime, regardless of what's set on
        // the TMP object in the Editor. This matters because the trainee's raw transcript is
        // shown as ONE unchunked line (unlike Sofia's, which is already split into short,
        // sentence-length chunks by SofiaPersona) - a long trainee sentence is far more likely
        // to hit the edge of the text box, and if Wrapping is off or Overflow is set to
        // Truncate, the last visible characters (often the closing quote mark added by
        // ShowLine below) get silently cut instead of wrapping to a second line.
#pragma warning disable CS0618 // enableWordWrapping is the older TMP API name - still functional, kept for broad TMP-version compatibility.
        if (subtitleText != null) subtitleText.enableWordWrapping = true;
#pragma warning restore CS0618
        if (subtitleText != null) subtitleText.overflowMode = TextOverflowModes.Overflow;

        // Start hidden/blank rather than whatever placeholder text was left on the
        // TMP object in the Editor.
        Clear();
    }

    // Called from SofiaPersona.OnTranscriptReceived with the trainee's own recognized speech.
    public void ShowTraineeLine(string text) => ShowLine(Speaker.Trainee, text);

    // Called from SofiaPersona.PlayNextInQueue with the exact chunk of text about to be spoken -
    // so the subtitle for Sofia's line appears in sync with when she actually starts saying it,
    // not when it was first generated/queued.
    public void ShowSofiaLine(string text) => ShowLine(Speaker.Sofia, text);

    // Not wired to anything yet - call this once the Virtual Assistant character exists,
    // from wherever ITS dialogue is dispatched to TTS (mirror the ShowSofiaLine() call site).
    public void ShowVirtualAssistantLine(string text) => ShowLine(Speaker.VirtualAssistant, text);

    public void ShowLine(Speaker speaker, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (subtitleText == null)
        {
            Debug.LogWarning("[SubtitleController] No subtitleText assigned - nothing to display into.");
            return;
        }

        // e.g. Trainee: "Hi, how are you"
        subtitleText.text = showSpeakerLabel ? $"{LabelFor(speaker)}: {text}" : text;
        if (subtitlePanel != null) subtitlePanel.SetActive(true);

        if (hideRoutine != null) StopCoroutine(hideRoutine);
        hideRoutine = StartCoroutine(HideAfterDelay());
    }

    private string LabelFor(Speaker speaker) => speaker switch
    {
        Speaker.Trainee => traineeLabel,
        Speaker.Sofia => sofiaLabel,
        Speaker.VirtualAssistant => virtualAssistantLabel,
        _ => speaker.ToString()
    };

    public void Clear()
    {
        if (hideRoutine != null)
        {
            StopCoroutine(hideRoutine);
            hideRoutine = null;
        }
        if (subtitleText != null) subtitleText.text = "";
        if (subtitlePanel != null) subtitlePanel.SetActive(false);
    }

    private IEnumerator HideAfterDelay()
    {
        yield return new WaitForSeconds(holdDuration);
        if (subtitleText != null) subtitleText.text = "";
        if (subtitlePanel != null) subtitlePanel.SetActive(false);
        hideRoutine = null;
    }
}