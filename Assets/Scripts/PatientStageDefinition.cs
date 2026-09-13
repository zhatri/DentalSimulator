using UnityEngine;

// One narrative beat of the patient's condition (e.g. "conscious and anxious"
// -> "early deterioration" -> "syncope event" -> "unresponsive" -> "recovering").
// A ScriptableObject so a tutor/author can create and edit these as assets in the
// Project window without touching SofiaPersona.cs - same "content as data, not code"
// pattern as ScenarioDefinition/ScenarioDatabase from section 9 of the project doc.
[CreateAssetMenu(fileName = "PatientStage", menuName = "Dental Simulator/Patient Stage")]
public class PatientStageDefinition : ScriptableObject
{
    [Header("Identity")]
    public string stageId = "stage_1_risk_factors";
    public int stageOrder = 0;

    [Header("What Sofia (the patient) currently is/feels - drives her system prompt")]
    [TextArea]
    public string scenarioStage = "conscious, nervous, pre-procedure";

    [TextArea]
    public string currentVitals = "BP 118/76, HR 92 (mildly elevated), alert and oriented, visibly anxious.";

    [Header("Optional: what she is allowed/not allowed to reveal in this stage")]
    [TextArea]
    public string stageGuardrail =
        "Do not mention losing consciousness, fainting, or feeling faint yet - that only " +
        "becomes true from the syncope stage onward. Stay anxious but coherent and responsive.";

    [Header("Optional: narration text for the trainee (e.g. a narrator/UI caption) when this stage begins")]
    [TextArea]
    public string narrationForTrainee =
        "Sally appears nervous as you greet her. She is sweating slightly and hesitant.";

    [Header("Conversation-driven advancement (optional)")]
    [Tooltip("Only enable this for stages where the PATIENT can meaningfully agree to move " +
             "forward through dialogue (e.g. confirming she's ready for the procedure). Leave " +
             "off for stages like the syncope event itself, which should be triggered by " +
             "something other than the patient's own speech (a trainee action, a timer, " +
             "the debug key) - she can't verbally consent to fainting.")]
    public bool conversationCanAdvanceStage = false;

    [TextArea]
    [Tooltip("Plain-language description of what the trainee needs to ask/say, and what Sofia's " +
             "answer needs to convey, for this stage to be considered complete. This gets " +
             "injected into Sofia's system prompt for this stage only - write it the way you'd " +
             "explain the condition to a person, e.g. \"The trainee asks if you're ready for the " +
             "injection/procedure, and you agree to proceed.\"")]
    public string advanceCondition =
        "The trainee explicitly asks if you are ready for the procedure, and you agree to proceed.";

    [Header("Deterministic keyword fallback (recommended - the LLM tag above is NOT reliably " +
            "emitted every time, this is not a bug, it's a real limit of LLM instruction-following)")]
    [Tooltip("If the trainee's own spoken words contain ANY of these phrases (case-insensitive, " +
             "checked before Sofia even replies), advance immediately - this never depends on the " +
             "LLM's output, so it can't be flaky the way the tag can be. List a few natural " +
             "phrasings a trainee might actually say for THIS stage's condition, e.g. " +
             "\"are you ready\", \"ready for the procedure\", \"shall we begin\".")]
    public string[] advanceTriggerPhrases = new string[] { "are you ready", "ready for the procedure" };

    [Header("Optional: fixed spoken response instead of an LLM-generated one")]
    [TextArea]
    [Tooltip("If set, Sofia will NOT ask the LLM to generate a reply when a trigger phrase above " +
             "is matched in the trainee's speech - she will speak this exact line instead (e.g. " +
             "\"Yes, I'm ready.\") and then advance immediately once it's queued. This is more " +
             "reliable than letting the LLM improvise a confirmation, since it removes any chance " +
             "of the model wandering off-script right at the moment of a stage transition. Leave " +
             "empty to keep the old behaviour: the LLM generates her in-character reply as usual.")]
    public string fixedAdvanceResponse = "";

    [Header("Trainee-action-driven advancement (optional) - the OTHER trigger path, for " +
            "stages that advance because the trainee DID something physical rather than said " +
            "something (give the injection, recline the chair, etc.). Independent of the " +
            "conversation fields above - a stage can use either, both, or neither.")]
    [Tooltip("Enable for stages where a physical trainee action (not dialogue) is what completes " +
             "this stage. An InteractableActions component on the relevant prop (right-clicked to " +
             "show its action menu, then left-clicked to choose one - see ActionMenuController) " +
             "calls PatientScenarioController.ReportTraineeAction(actionId) once the trainee picks " +
             "an option; this stage advances only if that id is listed below.")]
    public bool traineeActionCanAdvanceStage = false;

    [Tooltip("Action id(s) (matching one of an InteractableActions object's ActionOption.actionId " +
             "values, case-insensitive) that complete this stage, e.g. \"give_injection\" or " +
             "\"recline_chair\". Multiple ids means ANY of them advances the stage - list one per " +
             "action that would count.")]
    public string[] advanceTriggerActionIds = System.Array.Empty<string>();

    [Header("Stage-transition presentation (all optional - see PatientScenarioController)")]
    [Tooltip("Id of a pre-rendered, letterboxed cutscene (see CinematicCatalog) to play BEFORE " +
             "this stage begins - e.g. Sofia collapsing into syncope, or a treatment animation. " +
             "Leave blank to enter this stage with no cutscene at all. The video itself lives " +
             "outside Unity (e.g. AI-generated with Dreamina and hosted somewhere reachable by " +
             "URL) - this is just the id a tutor's cinematic catalog sheet maps to that URL, so " +
             "swapping or adding cutscenes never needs a Unity rebuild.")]
    public string cinematicId = "";

    [Tooltip("Animator trigger/parameter name to fire on Sofia when this stage begins, to switch " +
             "her pose/animation (e.g. \"SitDown\", \"GoUnconscious\", \"BeginTreatment\"). Leave " +
             "blank to leave her current animation state alone.")]
    public string sofiaPoseTrigger = "";

    [Tooltip("Id of a named scene position (see PositionMarkerRegistry, e.g. \"chair_seated\", " +
             "\"chair_reclined\") to teleport Sofia to the instant this stage begins. Leave blank " +
             "to leave her where she already is. Typically only needed on stages that also have a " +
             "cinematicId above, so the teleport happens hidden behind the cutscene rather than " +
             "visibly snapping her position in front of the trainee.")]
    public string sofiaPositionMarkerId = "";

    [Header("Virtual Assistant (bot) - a separate character the trainee can also talk to")]
    [TextArea]
    [Tooltip("Guidance content for the Virtual Assistant to draw on when the trainee talks to IT " +
             "and asks for help (any speech that doesn't match the confirmation phrase below counts " +
             "as asking for a hint - see VirtualAssistantPersona). The bot now has its own LlmAgent: " +
             "when one is assigned, this text is SEED/grounding content for its system prompt, not a " +
             "line it recites verbatim - it answers open questions (\"what should I do now?\") and " +
             "escalates to more direct guidance the more times the trainee asks on this stage. With " +
             "no LlmAgent assigned, the bot falls back to speaking this text verbatim, unchanged. " +
             "Leave blank to have the bot stay silent/say it has no guidance on this stage.")]
    public string stageHint = "";

    [Tooltip("Normally the conversational advance-trigger fields above (conversationCanAdvanceStage, " +
             "advanceTriggerPhrases, fixedAdvanceResponse) apply to SOFIA - the trainee asks/tells " +
             "HER something to advance. Check this instead for a stage where that same trigger " +
             "phrase/fixed-response pair belongs to the BOT - e.g. the trainee tells the Virtual " +
             "Assistant \"I'm ready\" during a calibration/readiness check, and it replies \"Okay, " +
             "let's begin the simulation.\" Sofia gets no advance-trigger wiring at all on a stage " +
             "where this is checked (she still gets her persona/vitals as normal, just no phrase " +
             "matching), and vice versa - only one of the two ever owns the confirmation per stage.")]
    public bool advanceConfirmationHandledByBot = false;
}
