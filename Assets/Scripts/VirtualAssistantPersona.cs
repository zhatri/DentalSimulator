using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using Meta.XR.BuildingBlocks.AIBlocks;

// The Virtual Assistant "bot" character - a SEPARATE object the trainee can hover+hold-
// click and talk to, distinct from Sofia. As of this revision it has its OWN full pipeline
// - LlmAgent, SpeechToTextAgent, TextToSpeechAgent - completely separate instances from
// Sofia's, per the team's explicit decision: sharing an Agent between two characters mixes
// conversation history/state, and the bot needed "a brain" to answer open-ended questions
// ("What should I do now?") and escalate its hints across repeated requests, rather than
// reciting one fixed line every time. It now does one of two things:
//
//   1. CONFIRMATION (still 100% deterministic, unchanged in behaviour from before this
//      revision): the trainee's words match this stage's advance trigger phrase (only when
//      PatientStageDefinition.advanceConfirmationHandledByBot is set for the current stage)
//      -> it speaks the stage's fixedAdvanceResponse line verbatim and raises
//      OnAdvanceIntentDetected, same as Sofia's fixed-advance-response path. This check
//      happens BEFORE the LLM is ever consulted and never depends on its output - per the
//      project's standing rule (see PatientScenarioController's header comment) that a
//      state transition this consequential must never be gated by free-form LLM output.
//      The bot's LLM is never involved in advancing the scenario, on purpose.
//   2. HINT / OPEN QUESTION (now LLM-driven): anything that isn't a confirmation-phrase
//      match is sent to the bot's own LlmAgent, with a system prompt built from the current
//      stage's stageHint content (used as seed/grounding material, not a literal line to
//      recite) plus an escalation instruction based on how many times the trainee has asked
//      for help on this stage already - see BuildHintSystemPrompt(). This lets the trainee
//      ask an open question like "What should I do now?" and get a real answer, and get
//      progressively more direct guidance on repeated asks instead of hearing the exact
//      same sentence every time.
//
// If no LlmAgent is assigned in the Inspector, this falls back to the old behaviour
// (speaking stageHint verbatim, no escalation, no open-ended Q&A) rather than failing
// outright - see OnTranscriptReceived.
public class VirtualAssistantPersona : MonoBehaviour
{
    [Header("AI Building Blocks - own instances, NOT shared with SofiaPersona")]
    [Tooltip("Optional. If assigned, hint/open-question requests are answered by this LLM " +
             "instead of the old fixed-line behaviour - see class comment. Leave unassigned " +
             "to keep the bot fully deterministic (it will just speak stageHint verbatim).")]
    [SerializeField] private LlmAgent llmAgent;
    [SerializeField] private SpeechToTextAgent speechToTextAgent;
    [SerializeField] private TextToSpeechAgent textToSpeechAgent;

    [Header("Hover + hold-to-talk (mirrors SofiaPersona's, own collider/camera)")]
    [Tooltip("Camera used to raycast for hover detection. Leave empty to use Camera.main.")]
    [SerializeField] private Camera interactionCamera;
    [Tooltip("Collider that defines the bot's hoverable area. Leave empty to use a Collider on " +
             "this same GameObject.")]
    [SerializeField] private Collider hoverCollider;
    [SerializeField] private LayerMask hoverLayerMask = ~0;
    [SerializeField] private float maxHoverDistance = 50f;

    [Header("Subtitles (optional)")]
    [SerializeField] private SubtitleController subtitleController;

    [Tooltip("Small pause before the bot speaks its FIXED confirmation line, in seconds - same " +
             "reasoning as SofiaPersona's fixedResponseDelay: without it, the bot's reply would " +
             "overwrite the trainee's own subtitle in the same frame it appeared, since that path " +
             "has no awaited network call to naturally space the two out. Not used for the LLM " +
             "hint path below - the network call itself provides that spacing.")]
    [SerializeField] private float responseDelay = 0.6f;

    // Per-stage content - set by PatientScenarioController.ApplyCurrentStage() on every
    // stage transition, exactly like SofiaPersona.SetStage(). currentHintText is now SEED
    // content for the LLM's system prompt (see BuildHintSystemPrompt), not a literal line
    // spoken outright, except in the no-LlmAgent fallback path.
    private string currentHintText = "";
    private bool confirmationCanAdvance = false;
    private string[] confirmationTriggerPhrases = Array.Empty<string>();
    private string confirmationResponse = "";

    // How many times the trainee has asked the bot for help on the CURRENT stage - drives
    // the escalation instruction in BuildHintSystemPrompt(). Reset to 0 on every SetStage()
    // call, so escalation is scoped per-stage, not across the whole scenario.
    private int hintRequestCount = 0;

    private bool isHovering = false;
    private bool isListening = false;

    // Exposed read-only so ReticleController can poll it, mirroring SofiaPersona.IsHovering -
    // see that field's own comment.
    public bool IsHovering => isHovering;

    // Guards against a second SendPromptAsync firing while one is already in flight for
    // this character - same reentrancy concern SofiaPersona guards against, and for the
    // identical reason (an STT agent emitting an interim transcript before the final one).
    private bool isAwaitingResponse = false;

    // Set while waiting for the confirmation line's TTS playback to actually finish, so
    // OnAdvanceIntentDetected fires only once the bot has genuinely finished SPEAKING it -
    // not the instant it's dispatched to TTS. Same bug/fix as SofiaPersona's
    // pendingStageAdvance: without this, a stage's cutscene/pose-change could start while
    // the confirmation line ("Okay, let's begin the simulation.") was still audibly playing.
    private bool pendingAdvanceAfterSpeech = false;

    // Raised ONLY from the deterministic confirmation path below. The LLM hint/open-question
    // path never raises this, on purpose - see class comment.
    public event Action OnAdvanceIntentDetected;

    private void OnEnable()
    {
        if (speechToTextAgent == null || textToSpeechAgent == null)
        {
            Debug.LogError("[VirtualAssistantPersona] SpeechToTextAgent/TextToSpeechAgent not assigned.");
            enabled = false;
            return;
        }

        speechToTextAgent.onTranscript.AddListener(OnTranscriptReceived);
        textToSpeechAgent.onSpeakFinished.AddListener(HandleSpeakFinished);

        if (llmAgent != null)
        {
            llmAgent.SystemPrompt = BuildHintSystemPrompt();
            llmAgent.onResponseReceived.AddListener(HandleResponseReceived);
        }
        else
        {
            Debug.LogWarning("[VirtualAssistantPersona] No LlmAgent assigned - falling back to " +
                              "speaking the stage's fixed stageHint text verbatim, with no " +
                              "escalation and no open-ended Q&A. Assign one to enable the full " +
                              "hint-brain behaviour.");
        }

        if (hoverCollider == null && GetComponent<Collider>() == null)
        {
            Debug.LogWarning("[VirtualAssistantPersona] No hoverCollider assigned and no Collider on " +
                              "this GameObject - hover-to-talk will never trigger.");
        }
    }

    private void OnDisable()
    {
        speechToTextAgent.onTranscript.RemoveListener(OnTranscriptReceived);
        textToSpeechAgent.onSpeakFinished.RemoveListener(HandleSpeakFinished);

        if (llmAgent != null)
            llmAgent.onResponseReceived.RemoveListener(HandleResponseReceived);
    }

    // Called by PatientScenarioController on every stage transition, mirroring
    // SofiaPersona.SetStage(). Signature is UNCHANGED from before this revision, so
    // PatientScenarioController.ApplyCurrentStage() needed no changes to call this.
    // newConfirmationCanAdvance/newConfirmationTriggerPhrases/newConfirmationResponse
    // should only be non-empty for a stage where
    // PatientStageDefinition.advanceConfirmationHandledByBot is true - the controller is
    // responsible for zeroing them out otherwise, so this class never needs to know about
    // Sofia at all.
    public void SetStage(string newHintText, bool newConfirmationCanAdvance,
                          string[] newConfirmationTriggerPhrases, string newConfirmationResponse)
    {
        currentHintText = newHintText ?? "";
        confirmationCanAdvance = newConfirmationCanAdvance;
        confirmationTriggerPhrases = newConfirmationTriggerPhrases ?? Array.Empty<string>();
        confirmationResponse = newConfirmationResponse ?? "";

        // New stage, new hint content - escalation starts fresh rather than carrying a
        // count over from whatever the trainee was asking about on the PREVIOUS stage.
        hintRequestCount = 0;

        if (llmAgent != null)
            llmAgent.SystemPrompt = BuildHintSystemPrompt();

        Debug.Log($"[VirtualAssistantPersona] Stage updated - hint seed content: " +
                  $"\"{(string.IsNullOrWhiteSpace(currentHintText) ? "(none)" : currentHintText)}\"");
    }

    private string trackingSessionId, trackingStageId;

    private void OnTranscriptReceived(string transcript)
    {
        if (PatientScenarioController.Instance == null || !PatientScenarioController.Instance.IsScenarioRunning) return;
        if (string.IsNullOrWhiteSpace(transcript))
        {
            Debug.LogWarning("[VirtualAssistantPersona] Empty transcript - ignoring.");
            return;
        }

        subtitleController?.ShowTraineeLine(transcript);

        if (isAwaitingResponse)
        {
            Debug.LogWarning($"[VirtualAssistantPersona] Ignoring transcript \"{transcript}\" - still awaiting a response to the previous turn.");
            return;
        }

        // Deterministic confirmation check FIRST, and it always wins - this never touches
        // the LLM even when one is assigned. See class comment for why this stays this way.
        trackingSessionId = SessionTelemetry.Instance?.Current?.sessionId;
        trackingStageId = SessionTelemetry.Instance?.CurrentStageId;
        SessionTelemetry.Instance?.Log("conversation", "trainee_to_assistant", transcript);
        bool confirmationMatched = confirmationCanAdvance && ContainsPhrase(transcript, confirmationTriggerPhrases);

        if (confirmationMatched && !string.IsNullOrWhiteSpace(confirmationResponse))
        {
            Debug.Log($"[VirtualAssistantPersona] Confirmation phrase matched - replying \"{confirmationResponse}\" and advancing.");
            isAwaitingResponse = true;
            StartCoroutine(SpeakConfirmationThenAdvance(confirmationResponse));
            return;
        }

        // Everything else is a hint / open-ended question. With an LlmAgent assigned, ask
        // it; otherwise fall back to the old fixed-line behaviour.
        if (llmAgent != null)
        {
            hintRequestCount++;
            SessionTelemetry.Instance?.Log("hint_requested", "trainee", transcript);
            llmAgent.SystemPrompt = BuildHintSystemPrompt();

            isAwaitingResponse = true;
            Debug.Log($"[VirtualAssistantPersona] Treating this as a hint/question (request #{hintRequestCount} this stage) - asking the bot's LLM.");
            _ = SendPromptSafely(transcript);
        }
        else if (!string.IsNullOrWhiteSpace(currentHintText))
        {
            Debug.Log("[VirtualAssistantPersona] No LlmAgent assigned - speaking the fixed hint line.");
            hintRequestCount++;
            SessionTelemetry.Instance?.Log("hint_requested", "trainee", transcript);
            isAwaitingResponse = true;
            StartCoroutine(SpeakLineNoAdvance(currentHintText));
        }
        else
        {
            Debug.Log("[VirtualAssistantPersona] Nothing to say for this stage (no hint text, no confirmation match, no LLM) - staying silent.");
        }
    }

    private async System.Threading.Tasks.Task SendPromptSafely(string transcript)
    {
        try
        {
            await llmAgent.SendPromptAsync(transcript);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[VirtualAssistantPersona] SendPromptAsync threw for transcript \"{transcript}\": {ex.Message}");
            isAwaitingResponse = false; // release the guard so the next turn isn't blocked forever
        }
    }

    // Bot's answers are short (1-2 sentences by design - see BuildHintSystemPrompt), so
    // unlike SofiaPersona this deliberately does NOT do sentence-by-sentence streaming
    // chunking - it just speaks the finished reply once it's fully in. Simpler, and fine
    // for a coaching aside rather than in-character dialogue meant to feel naturally paced.
    private void HandleResponseReceived(string fullText)
    {
        string line = (fullText ?? "").Trim();
        if (string.IsNullOrEmpty(line))
        {
            isAwaitingResponse = false;
            Debug.LogWarning("[VirtualAssistantPersona] LLM returned an empty response.");
            return;
        }

        SessionTelemetry.Instance?.LogDialogue(trackingSessionId, trackingStageId, "hint_disclosed", "assistant", line);
        subtitleController?.ShowVirtualAssistantLine(line);
        textToSpeechAgent.SpeakText(line);

        // Deliberately never invokes OnAdvanceIntentDetected here - the bot's LLM-generated
        // hint/answer content only ever informs the trainee, it NEVER gates stage
        // advancement. That stays exclusively on the deterministic confirmation-phrase path
        // above, unchanged from before this revision - consistent with the project's
        // standing rule (see PatientScenarioController's header comment) that consequential
        // state transitions must never be driven by free-form LLM output.
    }

    // Builds the bot's system prompt from the current stage's hint seed content and how
    // many times the trainee has asked this stage, so repeated requests get progressively
    // more direct guidance instead of the exact same sentence every time.
    private string BuildHintSystemPrompt()
    {
        string hintBlock = string.IsNullOrWhiteSpace(currentHintText)
            ? "No specific guidance has been authored for the current stage. If asked, say you " +
              "don't have specific guidance for this exact moment, and suggest the trainee " +
              "re-examine the patient and follow standard emergency-response protocol."
            : $"Guidance for the current stage (use this as the factual basis for every answer - " +
              $"do not invent clinical facts beyond it):\n{currentHintText}";

        string escalationInstruction = hintRequestCount <= 1
            ? "This is the trainee's FIRST time asking for help on this stage. Give a general, " +
              "subtle nudge in the right direction - do not just state the answer outright."
            : hintRequestCount == 2
                ? "This is the SECOND time the trainee has asked on this stage. Be noticeably more " +
                  "direct and specific than a first hint would be, narrowing down what they should do."
                : "The trainee has asked THREE OR MORE times on this stage. Stop being subtle - " +
                  "state plainly and explicitly, in plain instructions, exactly what they should do right now.";

        return $@"You are the Virtual Assistant, a supervisory AI helper bot in a VR dental
emergency-training simulation. You are NOT the patient and you are not a character in the
scenario - you are a coaching tool the trainee (a dental professional) can talk to for help,
entirely separate from the patient character (Sofia).

{hintBlock}

{escalationInstruction}

Respond in 1-2 short, clear sentences, in a calm, professional assistant tone. If the trainee
asked an actual question (e.g. ""What should I do now?""), answer it directly using the guidance
above. If they just asked for help generally, give a hint per the escalation instruction above.
Never reveal information about later stages of the scenario the trainee hasn't reached yet. Do
not break character as the assistant and do not mention you are a language model or an AI.";
    }

    // Deterministic confirmation path only - unchanged in behaviour from before this
    // revision. Same "wait a frame-spanning beat before speaking" fix as
    // SofiaPersona.RespondWithFixedAdvanceRoutine(), and for the identical reason - this
    // path has no awaited network call to naturally separate the trainee's subtitle from
    // the bot's reply.
    private IEnumerator SpeakConfirmationThenAdvance(string line)
    {
        yield return new WaitForSeconds(responseDelay);

        subtitleController?.ShowVirtualAssistantLine(line);
        SessionTelemetry.Instance?.LogDialogue(trackingSessionId, trackingStageId, "conversation", "assistant", line);
        pendingAdvanceAfterSpeech = true;
        textToSpeechAgent.SpeakText(line);

        // OnAdvanceIntentDetected is NOT invoked here - see HandleSpeakFinished(). Firing it
        // immediately after SpeakText() (which just dispatches the line and returns) used to
        // let AdvanceStage()'s cutscene/pose-change start while this line was still audibly
        // playing.
    }

    // Fires once the bot's TTS agent reports it has actually finished speaking - the real
    // completion signal, as opposed to SpeakText() merely having been called.
    private void HandleSpeakFinished()
    {
        isAwaitingResponse = false;
        if (!pendingAdvanceAfterSpeech) return;

        pendingAdvanceAfterSpeech = false;
        Debug.Log("[VirtualAssistantPersona] Finished speaking the confirmation line - advancing stage now.");
        OnAdvanceIntentDetected?.Invoke();
    }

    // Public, deliberately separate from every other speech path above - lets an outside
    // caller (ScenarioSelectionController, reading a scenario's scn_description aloud as the
    // trainee browses the dropdown) have the bot speak arbitrary text with none of this
    // class's own stage/hint/confirmation machinery involved: no LLM call, no advance
    // signal, no hintRequestCount bump, nothing that assumes a patient scenario is even
    // running yet - appropriate for the Welcome/Scenario Selection screen, which happens
    // BEFORE PatientScenarioController.BeginScenario() is ever called. Interrupts whatever
    // the bot was previously saying the same way every other SpeakText() call already does
    // (MBB's own TextToSpeechAgent behaviour, unchanged here) - expected and fine for reading
    // a fresh description the instant the trainee changes the dropdown selection.
    public void SpeakAnnouncement(string text)
    {
        if (textToSpeechAgent == null)
        {
            Debug.LogWarning("[VirtualAssistantPersona] SpeakAnnouncement() called but no TextToSpeechAgent is assigned - ignoring.");
            return;
        }
        if (string.IsNullOrWhiteSpace(text)) return;

        subtitleController?.ShowVirtualAssistantLine(text);
        textToSpeechAgent.SpeakText(text);
    }

    // No-LlmAgent fallback path only (see OnTranscriptReceived) - speaks stageHint verbatim,
    // never advances.
    private IEnumerator SpeakLineNoAdvance(string line)
    {
        yield return new WaitForSeconds(responseDelay);
        SessionTelemetry.Instance?.LogDialogue(trackingSessionId, trackingStageId, "hint_disclosed", "assistant", line);

        subtitleController?.ShowVirtualAssistantLine(line);
        textToSpeechAgent.SpeakText(line);
    }

    private static bool ContainsPhrase(string transcript, string[] phrases)
    {
        if (phrases == null || phrases.Length == 0) return false;

        string lower = transcript.ToLowerInvariant();
        foreach (string phrase in phrases)
        {
            if (!string.IsNullOrWhiteSpace(phrase) && lower.Contains(phrase.ToLowerInvariant()))
                return true;
        }
        return false;
    }

    private void Update()
    {
        UpdateHoverState();

        if (Mouse.current == null) return;

        bool leftPressedThisFrame = Mouse.current.leftButton.wasPressedThisFrame;
        bool leftReleasedThisFrame = Mouse.current.leftButton.wasReleasedThisFrame;
        bool leftHeld = Mouse.current.leftButton.isPressed;

        // Also refuses to start while ActionMenuController's right-click menu is open, OR while
        // the Welcome/Scenario Selection calibration UI is up - see the identical guard/comment
        // in SofiaPersona.Update().
        if (isHovering && leftPressedThisFrame && !isListening && !ActionMenuController.AnyMenuOpen
            && !WelcomeScreenController.IsCalibrationActive && !QuitConfirmationController.IsOpen
            && !IncorrectActionController.IsOpen)
        {
            isListening = true;
            speechToTextAgent.StartListening();
        }

        if (isListening && (leftReleasedThisFrame || !leftHeld || !isHovering))
        {
            isListening = false;
            speechToTextAgent.StopNow();
        }
    }

    private void UpdateHoverState()
    {
        Camera cam = interactionCamera != null ? interactionCamera : Camera.main;
        if (cam == null || Mouse.current == null)
        {
            isHovering = false;
            return;
        }

        Vector2 mousePos = Mouse.current.position.ReadValue();
        Ray ray = cam.ScreenPointToRay(mousePos);

        if (Physics.Raycast(ray, out RaycastHit hit, maxHoverDistance, hoverLayerMask))
        {
            Collider expected = hoverCollider != null ? hoverCollider : GetComponent<Collider>();
            isHovering = expected != null && hit.collider == expected;
        }
        else
        {
            isHovering = false;
        }
    }
}
