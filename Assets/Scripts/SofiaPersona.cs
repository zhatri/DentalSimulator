using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;
using System.Collections.Generic;
using Meta.XR.BuildingBlocks.AIBlocks;

public class SofiaPersona : MonoBehaviour
{
    [Header("AI Building Blocks (drag from this GameObject or the scene)")]
    [SerializeField] private LlmAgent llmAgent;
    [SerializeField] private SpeechToTextAgent speechToTextAgent;
    [SerializeField] private TextToSpeechAgent textToSpeechAgent;

    [Header("Patient persona (Inspector defaults - used until/unless a scenario overrides " +
            "them at runtime, see ApplyScenarioPersona())")]
    [SerializeField] private string patientName = "Sofia Lavigne";
    [SerializeField] private string medicalHistory =
        "Penicillin allergy (confirmed), no other known allergies.";

    // Populated by ApplyScenarioPersona() from the backend's SCENARIO.scn_patient_profile - a
    // single free-text block ("Sofia Lavigne is a 27 years old patient that has a penicillin
    // alergy, has a warm personality, open minded") rather than separate name/history fields,
    // matching how the tutor portal's "Edit Scenario" form actually authors it (one Patient
    // Profile textarea, not per-field inputs). When set, this REPLACES the patientName/
    // medicalHistory pair above in the prompt entirely rather than being merged with them -
    // trying to splice one flat authored paragraph back into two rigid slots would be guesswork
    // (which words are the "name" vs "history"?) that the portal's own free-text field was
    // presumably chosen specifically to avoid. Left blank, the Inspector fields above still work
    // exactly as before for local testing with no backend/portal involved.
    private string patientProfile = "";

    // Populated by ApplyScenarioPersona() from SCENARIO.scn_guardrail - a SCENARIO-wide
    // guardrail (e.g. "Pretend as a real human patient, never reveal about AI usage") that
    // applies for the whole run, distinct from stageGuardrail below (STAGE.stg_guardrail, which
    // changes every stage transition, e.g. "don't mention fainting yet"). Kept as a separate
    // field/prompt block rather than merged into stageGuardrail so a future SetStage() call
    // can never accidentally clear it - the portal already models these as two different
    // columns on two different tables for exactly this reason.
    private string scenarioGuardrail = "";
    // These three now represent the STARTING stage only - SetStage() updates them at
    // runtime as PatientScenarioController walks through the scenario's stages, so
    // Sofia's persona actually changes as the emergency progresses instead of staying
    // fixed at whatever was set here at Awake().
    [TextArea]
    [SerializeField] private string currentVitals =
        "BP 90/60 (dropping), HR 118, mild facial swelling, difficulty breathing.";
    [SerializeField] private string scenarioStage =
        "early deterioration (2 min post-injection)";
    [TextArea]
    [SerializeField] private string stageGuardrail = "";
    private bool conversationCanAdvanceStage = false;
    private string advanceCondition = "";
    private string[] advanceTriggerPhrases = System.Array.Empty<string>();
    // When set for the current stage, a matched advance phrase makes Sofia speak this exact
    // line instead of calling the LLM at all - see RespondWithFixedAdvance().
    private string fixedAdvanceResponse = "";

    // Guards against firing OnAdvanceIntentDetected twice for the same trainee turn -
    // once from the deterministic phrase match (checked immediately on transcript) and
    // again later from the LLM tag (checked when the response arrives). Reset per turn.
    private bool advancedThisTurn = false;
    private bool pendingPhraseAdvance = false;

    // Set the instant this turn is DECIDED to advance the stage, but OnAdvanceIntentDetected
    // itself isn't invoked until PlayNextInQueue() sees the speech queue actually drain to
    // empty (i.e. Sofia has finished SPEAKING the line, not just had it dispatched to TTS).
    // Without this gap, AdvanceStage()'s cutscene/pose-change/persona-swap used to fire the
    // instant the line was queued, while the audio was still playing - visibly colliding with
    // a cutscene starting in front of her mid-sentence. See PlayNextInQueue().
    private bool pendingStageAdvance = false;

    // Tags Sofia is instructed to append to her own reply - never spoken aloud, stripped
    // before TTS/display. Same pattern as the [READY_CONFIRMED]/[NOT_READY]/[UNCLEAR]
    // tags in the lightbulb assistant's readiness check (section 9): a free-form reply
    // never gates a state change directly, but a required, always-present tag can be
    // parsed deterministically. Requiring the model to ALWAYS emit one of the two (never
    // silently omit both) is what makes a missing tag detectable as a parsing problem
    // rather than indistinguishable from "not ready yet".
    private const string AdvanceTag = "[STAGE_ADVANCE]";
    private const string ContinueTag = "[STAGE_CONTINUE]";

    // Raised when Sofia's reply carries [STAGE_ADVANCE]. PatientScenarioController
    // subscribes to this and decides what to do - SofiaPersona itself never calls
    // AdvanceStage() directly, keeping the same one-way dependency (controller owns
    // persona, not the reverse) as the rest of this design.
    public event System.Action OnAdvanceIntentDetected;

    [Header("Hover + hold-to-talk (mouse, for Simulator/Editor testing)")]
    [Tooltip("Camera used to raycast for hover detection. Leave empty to use Camera.main.")]
    [SerializeField] private Camera interactionCamera;
    [Tooltip("Collider that defines Sofia's hoverable area (e.g. a Capsule/Box Collider on her " +
             "model or a child object). Leave empty to use a Collider on this same GameObject.")]
    [SerializeField] private Collider hoverCollider;
    [SerializeField] private LayerMask hoverLayerMask = ~0;
    [SerializeField] private float maxHoverDistance = 50f;

    private bool isHovering = false;
    private bool isListening = false;

    // Exposed read-only so ReticleController (or anything else that just needs to know
    // "is the cursor over Sofia right now," without duplicating this class's own hover
    // raycast) can poll it - see ReticleController.LateUpdate().
    public bool IsHovering => isHovering;

    [Header("Subtitles (optional)")]
    [Tooltip("If assigned, the trainee's recognized speech and Sofia's spoken lines are both " +
             "shown here as they happen. Leave empty to run without subtitles.")]
    [SerializeField] private SubtitleController subtitleController;

    [Tooltip("Small pause before Sofia speaks a fixed advance response (see fixedAdvanceResponse " +
             "on the current stage), in seconds. Without this, her line would overwrite the " +
             "trainee's subtitle in the very same frame it appeared in - since the fixed-response " +
             "path has no network call to await, nothing gives Unity a chance to render a frame " +
             "with the trainee's line before it gets replaced. Also reads more naturally as a " +
             "brief 'thinking' beat rather than an instant, robotic reply.")]
    [SerializeField] private float fixedResponseDelay = 0.6f;

    private readonly Queue<string> speechQueue = new();
    private string lastStreamedText = string.Empty;
    private string lastSpokenChunk = string.Empty;
    private int spokenLength = 0;
    private bool isSpeaking = false;
    private bool isAwaitingResponse = false; // guards against onTranscript firing again mid-turn
    private static readonly char[] SentenceEnders = { '.', '!', '?' };

    // Moved wiring to OnEnable/OnDisable (matching the original MBB setup guide's pattern)
    // instead of Awake with no cleanup. Awake-only wiring is the classic cause of a
    // "used to work, now doubles/triples" regression in the Editor: if Enter Play Mode
    // Options has "Reload Domain" disabled (a common speed setting), pressing Play again
    // without a full recompile does NOT clear old UnityEvent listener subscriptions on
    // objects that survive between Play sessions - each extra Play press stacks another
    // duplicate listener on the same event, so turn N produces N spoken responses.
    // OnEnable/OnDisable self-corrects because the listener is always removed before
    // being re-added, regardless of how many times Play has been pressed.
    private void OnEnable()
    {
        if (llmAgent == null || speechToTextAgent == null || textToSpeechAgent == null)
        {
            Debug.LogError("[SofiaPersona] One or more Agent references are not assigned in the Inspector.");
            enabled = false;
            return;
        }

        llmAgent.SystemPrompt = BuildSystemPrompt();

        speechToTextAgent.onTranscript.AddListener(OnTranscriptReceived);
        llmAgent.onStreamingDelta.AddListener(HandleStreamingDelta);
        llmAgent.onResponseReceived.AddListener(HandleResponseReceived);
        textToSpeechAgent.onSpeakFinished.AddListener(PlayNextInQueue);

        if (hoverCollider == null && GetComponent<Collider>() == null)
        {
            Debug.LogWarning("[SofiaPersona] No hoverCollider assigned and no Collider on this " +
                              "GameObject - hover-to-talk will never trigger. Add a Collider (e.g. " +
                              "a Capsule Collider sized to Sofia's model) and either leave it on this " +
                              "object or assign it to hoverCollider.");
        }
    }

    private void OnDisable()
    {
        speechToTextAgent.onTranscript.RemoveListener(OnTranscriptReceived);
        llmAgent.onStreamingDelta.RemoveListener(HandleStreamingDelta);
        llmAgent.onResponseReceived.RemoveListener(HandleResponseReceived);
        textToSpeechAgent.onSpeakFinished.RemoveListener(PlayNextInQueue);
    }

    private void OnTranscriptReceived(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            Debug.LogWarning("[SofiaPersona] Empty transcript - not sending to LLM.");
            return;
        }

        // Show the trainee's own recognized words immediately, regardless of what happens
        // next (fixed response, LLM call, or the re-entrancy guard below) - this is what
        // the trainee said, so it's correct to display even if this turn ends up ignored.
        subtitleController?.ShowTraineeLine(transcript);

        // Guard: if onTranscript ever fires more than once for the same utterance
        // (some STT agents emit an interim result before the final one), this stops
        // a second, overlapping SendPromptAsync call from firing a second full reply -
        // a second, entirely independent cause of "it responded twice" that has
        // nothing to do with the streaming/sentence-chunking behaviour below.
        if (isAwaitingResponse)
        {
            Debug.LogWarning($"[SofiaPersona] Ignoring transcript \"{transcript}\" - still awaiting a response to the previous turn.");
            return;
        }

        advancedThisTurn = false;
        pendingPhraseAdvance = false;

        bool phraseMatched = conversationCanAdvanceStage && ContainsAdvanceTriggerPhrase(transcript);

        // If this stage has an authored fixed response AND the trigger phrase matched, skip
        // the LLM call entirely - speak the exact authored line and advance. This is more
        // reliable than letting the model improvise a confirmation: there's no chance of it
        // wandering into next-stage content, adding detail it shouldn't know yet, or phrasing
        // "ready" ambiguously right at the moment of a stage transition.
        if (phraseMatched && !string.IsNullOrWhiteSpace(fixedAdvanceResponse))
        {
            Debug.Log($"[SofiaPersona] Advance phrase matched - using fixed response instead of the LLM: \"{fixedAdvanceResponse}\"");
            StartCoroutine(RespondWithFixedAdvanceRoutine());
            return;
        }

        isAwaitingResponse = true;
        lastStreamedText = string.Empty;
        spokenLength = 0;
        lastSpokenChunk = string.Empty;

        // Deterministic check, on the TRAINEE's own words, done immediately. But do NOT
        // switch stages yet - only flag that we should, once Sofia has actually answered
        // THIS turn's question in her CURRENT stage's persona. Advancing immediately here
        // would reassign llmAgent.SystemPrompt to the next stage before the prompt for
        // this question is even sent, so she'd answer as the next stage instead of
        // acknowledging what was actually asked - that's exactly the "no answer, jumps
        // straight to the next stage" symptom. HandleResponseReceived below does the
        // actual advance, after her reply to THIS stage exists.
        if (phraseMatched)
        {
            Debug.Log($"[SofiaPersona] Deterministic advance phrase matched in trainee speech: \"{transcript}\" - will advance after she replies.");
            pendingPhraseAdvance = true;
        }

        _ = SendPromptSafely(transcript);
    }

    // Speaks the stage's authored fixed line instead of calling the LLM, then advances once
    // it's queued for speech. Bypasses isAwaitingResponse entirely - there's no async call in
    // flight here for it to guard against, so gating on it would just block this path for no
    // reason (isAwaitingResponse only protects against overlapping SendPromptAsync calls).
    //
    // Runs after a short delay (fixedResponseDelay) rather than immediately. Without it, this
    // whole method would execute in the SAME frame as ShowTraineeLine() back in
    // OnTranscriptReceived - FlushRemainder() -> EnqueueSpeech() -> PlayNextInQueue() calls
    // ShowSofiaLine(), which overwrites the subtitle text before Unity ever renders a frame
    // with the trainee's line in it. That's the confirmed cause of the trainee's subtitle
    // "showing nothing" specifically on advance-triggering turns - the LLM-driven path doesn't
    // have this problem because awaiting the network call naturally spans many frames.
    private IEnumerator RespondWithFixedAdvanceRoutine()
    {
        yield return new WaitForSeconds(fixedResponseDelay);

        spokenLength = 0;
        lastSpokenChunk = string.Empty;
        lastStreamedText = fixedAdvanceResponse;
        FlushRemainder();

        if (!advancedThisTurn)
        {
            advancedThisTurn = true;
            pendingStageAdvance = true;
            Debug.Log("[SofiaPersona] Fixed response queued - will advance once Sofia actually finishes speaking it (source: fixed advance response).");
        }
    }

    private bool ContainsAdvanceTriggerPhrase(string transcript)
    {
        if (advanceTriggerPhrases == null || advanceTriggerPhrases.Length == 0) return false;

        string lowerTranscript = transcript.ToLowerInvariant();
        foreach (string phrase in advanceTriggerPhrases)
        {
            if (!string.IsNullOrWhiteSpace(phrase) && lowerTranscript.Contains(phrase.ToLowerInvariant()))
                return true;
        }
        return false;
    }

    private async System.Threading.Tasks.Task SendPromptSafely(string transcript)
    {
        try
        {
            await llmAgent.SendPromptAsync(transcript);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[SofiaPersona] SendPromptAsync threw for transcript \"{transcript}\": {ex.Message}");
            isAwaitingResponse = false; // release the guard so the next turn isn't blocked forever
        }
    }

    private void HandleStreamingDelta(string accumulatedText)
    {
        lastStreamedText = accumulatedText;

        int lastEnderIndex = accumulatedText.LastIndexOfAny(SentenceEnders);
        if (lastEnderIndex < spokenLength) return;

        string newChunk = accumulatedText.Substring(spokenLength, lastEnderIndex + 1 - spokenLength).Trim();
        spokenLength = lastEnderIndex + 1;

        if (string.IsNullOrEmpty(newChunk)) return;
        if (newChunk == lastSpokenChunk) return;

        lastSpokenChunk = newChunk;
        EnqueueSpeech(newChunk);
    }

    // Bug fix: this used to be `_ => FlushRemainder()`, discarding the event's own
    // string argument and relying entirely on `lastStreamedText`, which is ONLY ever
    // set inside HandleStreamingDelta. With Enable Streaming unchecked, that method
    // never runs, so lastStreamedText stays "" forever and nothing could ever be
    // spoken - this is the confirmed cause of "no response at all" with streaming off.
    // Using the event's own fullText as ground truth fixes that unconditionally,
    // and also removes any dependency on delta-timing for the streaming-on case.
    private void HandleResponseReceived(string fullText)
    {
        string cleanText = fullText;

        bool tagRequestsAdvance = false;

        if (conversationCanAdvanceStage)
        {
            bool advanceTagFound = cleanText.Contains(AdvanceTag);
            bool continueTagFound = cleanText.Contains(ContinueTag);

            // Strip both possible tags before anything gets spoken or counted toward
            // lastStreamedText - this must happen BEFORE FlushRemainder() runs, or the
            // tag text itself gets treated as "unspoken remainder" and read aloud.
            cleanText = cleanText.Replace(AdvanceTag, "").Replace(ContinueTag, "").TrimEnd();

            if (advanceTagFound)
            {
                tagRequestsAdvance = true;
            }
            else if (!continueTagFound)
            {
                // Neither tag appeared - the model didn't follow the instruction this turn.
                // Not fatal (the manual debug-key/trigger fallback, and the phrase check,
                // still work), but worth knowing about if advancement seems inconsistent.
                Debug.LogWarning("[SofiaPersona] Expected a stage-advance tag but found neither " +
                                  "[STAGE_ADVANCE] nor [STAGE_CONTINUE] in the response - check the system prompt.");
            }
        }

        lastStreamedText = cleanText;
        FlushRemainder();
        isAwaitingResponse = false;

        // Only NOW - after her reply text has been fully computed and queued for speech -
        // is it safe to switch personas for the next turn. Either signal (the phrase match
        // from OnTranscriptReceived, or the tag found just above) can trigger it; whichever
        // fires first wins, and advancedThisTurn stops the other from firing a second time.
        // The actual OnAdvanceIntentDetected invocation is deferred further still - see
        // pendingStageAdvance's comment and PlayNextInQueue() - so this reply is fully SPOKEN,
        // not just queued, before anything (a cutscene, a pose change, Sofia's own persona
        // swap) happens on top of her.
        if ((pendingPhraseAdvance || tagRequestsAdvance) && !advancedThisTurn)
        {
            advancedThisTurn = true;
            pendingPhraseAdvance = false;
            pendingStageAdvance = true;
            string source = tagRequestsAdvance ? "LLM tag" : "trainee phrase match";
            Debug.Log($"[SofiaPersona] Reply queued - will advance once Sofia actually finishes speaking it (source: {source}).");
        }
    }

    private void FlushRemainder()
    {
        if (spokenLength < lastStreamedText.Length)
        {
            string remainder = lastStreamedText.Substring(spokenLength).Trim();
            if (!string.IsNullOrEmpty(remainder) && remainder != lastSpokenChunk)
            {
                lastSpokenChunk = remainder;
                EnqueueSpeech(remainder);
            }
        }
    }

    private void EnqueueSpeech(string text)
    {
        speechQueue.Enqueue(text);
        if (!isSpeaking) PlayNextInQueue();
    }

    private void PlayNextInQueue()
    {
        if (speechQueue.Count == 0)
        {
            isSpeaking = false;

            // This is the moment Sofia has actually finished speaking everything queued for
            // the turn - PlayNextInQueue is what onSpeakFinished calls after each chunk's
            // audio completes, so hitting an empty queue here (rather than mid-turn, between
            // streamed sentences) means there's genuinely nothing left to say. Safe to fire
            // the deferred advance now: whatever set pendingStageAdvance (HandleResponseReceived
            // or RespondWithFixedAdvanceRoutine) had already enqueued every chunk for this turn
            // before setting the flag, so there's no risk of a later chunk still queuing up
            // after this fires.
            if (pendingStageAdvance)
            {
                pendingStageAdvance = false;
                Debug.Log("[SofiaPersona] Finished speaking - advancing stage now.");
                OnAdvanceIntentDetected?.Invoke();
            }

            return;
        }
        isSpeaking = true;

        string next = speechQueue.Dequeue();
        // Shown at the moment this chunk is actually dispatched to TTS, not when it was
        // generated/queued - so the subtitle for a given sentence appears in sync with
        // Sofia actually starting to say it, including the fixed-advance-response path
        // (RespondWithFixedAdvance funnels through FlushRemainder -> EnqueueSpeech -> here
        // just like any LLM-generated line, so it needs no separate subtitle hook).
        subtitleController?.ShowSofiaLine(next);
        textToSpeechAgent.SpeakText(next);
    }

    private void Update()
    {
        UpdateHoverState();

        if (Mouse.current == null) return;

        bool leftPressedThisFrame = Mouse.current.leftButton.wasPressedThisFrame;
        bool leftReleasedThisFrame = Mouse.current.leftButton.wasReleasedThisFrame;
        bool leftHeld = Mouse.current.leftButton.isPressed;

        // Only START listening if the click itself lands while hovering Sofia - clicking
        // elsewhere and dragging onto her while held does NOT start a turn, matching how
        // push-to-talk buttons normally work (you press the button, not "end up over it").
        // Also refuses to start while ActionMenuController's right-click menu is open, so a
        // left-click meant for a menu button can't also be read as "start talking to Sofia"
        // if her collider happens to be underneath the menu on screen.
        if (isHovering && leftPressedThisFrame && !isListening && !ActionMenuController.AnyMenuOpen)
        {
            isListening = true;
            speechToTextAgent.StartListening();
        }

        // STOP listening on mouse-up, OR if the cursor leaves Sofia's collider while still
        // held down - without this second condition, dragging off her model mid-sentence
        // would leave the mic open indefinitely since wasReleasedThisFrame would never fire.
        if (isListening && (leftReleasedThisFrame || !leftHeld || !isHovering))
        {
            isListening = false;
            speechToTextAgent.StopNow();
        }
    }

    // Casts a ray from the interaction camera through the current mouse position and
    // checks whether it hits Sofia's own collider specifically (not just "something") -
    // so hovering over a wall or another NPC in front of/behind her doesn't count.
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

    // Called by PatientScenarioController on every stage transition. This is the
    // narrative-progression hook: each call re-grounds Sofia in the CURRENT beat of
    // the scenario rather than leaving her stuck on whatever the Inspector said at
    // Awake(). Deliberately does not decide WHEN to advance - that decision stays
    // outside this class, driven deterministically (see PatientScenarioController).
    public void SetStage(string newScenarioStage, string newVitals, string newGuardrail = "",
                          bool newConversationCanAdvanceStage = false, string newAdvanceCondition = "",
                          string[] newAdvanceTriggerPhrases = null, string newFixedAdvanceResponse = "")
    {
        scenarioStage = newScenarioStage;
        currentVitals = newVitals;
        stageGuardrail = newGuardrail;
        conversationCanAdvanceStage = newConversationCanAdvanceStage;
        advanceCondition = newAdvanceCondition;
        advanceTriggerPhrases = newAdvanceTriggerPhrases ?? System.Array.Empty<string>();
        fixedAdvanceResponse = newFixedAdvanceResponse ?? "";

        llmAgent.SystemPrompt = BuildSystemPrompt();

        // IMPORTANT - verify this empirically before relying on it: some chat-style LLM
        // wrappers only apply SystemPrompt once, at the start of a conversation, and then
        // keep an internal running history where later turns no longer re-read it. If
        // Sofia's answers don't actually change after a stage transition despite this log
        // firing, check whether LlmAgent exposes a history-reset method (look in the
        // Inspector / IntelliSense for something like ClearHistory()/ResetConversation())
        // and call it here too - otherwise stale context from the previous stage can keep
        // leaking into her answers even though SystemPrompt itself is now correct.
        Debug.Log($"[SofiaPersona] Stage updated -> \"{scenarioStage}\" | vitals: \"{currentVitals}\"");
    }

    // Called once, early - ideally before the first SetStage() call so the very first system
    // prompt already reflects it, but safe to call any time since it just rebuilds the prompt
    // from current state either way (SetStage() does the same). See RemoteScenarioApiLoader,
    // which now fetches GET /api/scenarios/{id} and forwards the result here via
    // PatientScenarioController.ApplyScenarioPersona() - this is what lets a tutor change which
    // patient Sofia plays entirely from the portal, with no Unity rebuild, closing the gap where
    // patientName/medicalHistory above were previously Inspector-only/compile-time constants.
    public void ApplyScenarioPersona(string newPatientProfile, string newScenarioGuardrail)
    {
        if (!string.IsNullOrWhiteSpace(newPatientProfile))
            patientProfile = newPatientProfile.Trim();
        scenarioGuardrail = newScenarioGuardrail ?? "";

        llmAgent.SystemPrompt = BuildSystemPrompt();
        Debug.Log($"[SofiaPersona] Scenario persona applied from backend - patientProfile " +
                   $"{(string.IsNullOrWhiteSpace(patientProfile) ? "still blank, using Inspector patientName/medicalHistory" : $"set ({patientProfile.Length} chars)")}, " +
                   $"scenarioGuardrail {(string.IsNullOrWhiteSpace(scenarioGuardrail) ? "blank" : "set")}.");
    }

    private string BuildSystemPrompt()
    {
        // Free-text portal-authored profile takes over the WHOLE persona-intro line when
        // present, rather than being merged with the Inspector name/history pair - see
        // patientProfile's own field comment for why splicing the two would be guesswork.
        string personaIntro = !string.IsNullOrWhiteSpace(patientProfile)
            ? patientProfile
            : $"You are {patientName}, a patient in a dental chair.\nMedical history: {medicalHistory}";

        string scenarioGuardrailBlock = string.IsNullOrWhiteSpace(scenarioGuardrail)
            ? ""
            : $"\n\nImportant (applies for the whole scenario): {scenarioGuardrail}";

        string guardrailBlock = string.IsNullOrWhiteSpace(stageGuardrail)
            ? ""
            : $"\n\nImportant: {stageGuardrail}";

        // Only instruct Sofia to tag her replies when THIS stage is actually meant to be
        // conversation-advanceable - stages like the syncope event never get this block,
        // so there's no risk of an accidental [STAGE_ADVANCE] firing during a stage where
        // advancement is supposed to come from something else entirely.
        string advanceInstructionBlock = "";
        if (conversationCanAdvanceStage && !string.IsNullOrWhiteSpace(advanceCondition))
        {
            advanceInstructionBlock = $@"

MANDATORY SYSTEM TAG (this is separate from, and in addition to, your one-sentence in-character
reply above - the sentence-length rule does not apply to this line):
On its own new line, after your spoken sentence, output the literal exact text {AdvanceTag} if
the following has just become true from this exchange, otherwise output the literal exact text
{ContinueTag}: {advanceCondition}
You must output exactly one of these two exact strings, every single time, with no other words
on that line. This line is read by software, not spoken aloud, and is not part of your character's
dialogue - it does not count toward your sentence limit and is never a violation of it.";
        }

        return $@"{personaIntro}

Currently experiencing: {scenarioStage}
Current vitals: {currentVitals}

Respond in character, in 1-2 short sentences, showing appropriate
distress for this stage. Do not break character or mention you are an AI.{scenarioGuardrailBlock}{guardrailBlock}{advanceInstructionBlock}";
    }
}
