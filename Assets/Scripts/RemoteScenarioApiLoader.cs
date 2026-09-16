using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

// Replaces RemoteScenarioLoader (the Google Sheets/CSV loader) now that the team has a
// real backend: a small local Flask server, serving JSON from http://127.0.0.1:5000 by
// default, backed by the tables in Database.xlsx. Same job, same two-gate contract with
// PatientScenarioController (WaitForRemoteContent / SetStageSequence / UseFallbackStageSequence)
// as the CSV loader had - PatientScenarioController needed ZERO changes for this swap,
// which is exactly what keeping "where stage data comes from" decoupled from "what a
// stage IS" (section 16) was for.
//
// Also fetches GET /api/scenarios (the full SCENARIO list - there is no single-item
// /api/scenarios/{id} endpoint, by design: this is the same list a future calibration/
// scenario-selection screen will fetch to show the trainee their choices, see
// FetchAndApplyScenarioPersona()'s own comment for why), picks out the row matching this
// component's scenarioId, and applies its Patient Profile/Guardrail to Sofia via
// PatientScenarioController.ApplyScenarioPersona() BEFORE the STAGE fetch below. This is a
// second, independent responsibility bolted onto what was originally just a stage loader,
// because it's the same backend and the same "tutor edits it in the portal, Unity just needs
// to reflect it" pattern - splitting it into a separate MonoBehaviour would just mean wiring up
// a second Backend Config/Scenario Id pair in the Inspector for no real benefit.
//
// FIELD MAPPING from STAGE's columns to PatientStageDefinition, and the assumptions
// behind it (worked out from the ONE example row currently in Database.xlsx - re-check
// these once there's more real content, and correct this comment/the mapping below if
// any of them are wrong):
//
//   scenarioStage        <- stg_prompt          (what Sofia currently is/feels)
//   currentVitals        <- stg_vital
//   stageGuardrail       <- stg_guardrail
//   advanceCondition     <- SYNTHESIZED from stg_trigger_phrase (no dedicated column - see
//                                                  ToStageDefinition()), rather than left blank.
//                                                  Feeds Sofia's optional LLM-tag advance path
//                                                  (BuildSystemPrompt()'s [STAGE_ADVANCE]/
//                                                  [STAGE_CONTINUE] instruction), which is now
//                                                  ADDITIVE to, not a replacement for, the
//                                                  deterministic exact-phrase/fixed-response path
//                                                  below - added because the exact-phrase check
//                                                  alone was confirmed too rigid for real trainee
//                                                  speech (September 2026, see project doc section
//                                                  34). Only applies to Sofia-owned confirmations
//                                                  today - PatientScenarioController doesn't pass
//                                                  advanceCondition to VirtualAssistantPersona.
//   narrationForTrainee  <- NOT populated - no matching column exists in STAGE yet, stays
//                                                  blank until one's added
//   stageHint            <- stg_bot_hint          (confirmed: guidance content for the Virtual
//                                                  Assistant bot if the trainee asks it for a
//                                                  hint - see VirtualAssistantPersona. As of
//                                                  the bot's own-LlmAgent redesign this is SEED
//                                                  content for its system prompt, not a fixed
//                                                  line recited verbatim, and it's still NOT an
//                                                  advance condition - the bot's LLM output
//                                                  never gates stage advancement, only the
//                                                  deterministic confirmation-phrase path below
//                                                  does that)
//   advanceConfirmationHandledByBot <- true whenever stg_trigger contains "bot" (case-
//                                                  insensitive) - e.g. the trainee tells the
//                                                  bot "I'm ready" and IT confirms readiness,
//                                                  rather than Sofia handling that exchange
//   conversationCanAdvanceStage <- true whenever stg_trigger_phrase OR stg_trigger_answer
//                                                  is non-blank. This phrase/response pair is
//                                                  shared between Sofia and the bot -
//                                                  advanceConfirmationHandledByBot decides
//                                                  which one it's actually wired to (see
//                                                  PatientScenarioController.ApplyCurrentStage)
//   advanceTriggerPhrases <- stg_trigger_phrase, split on ';' (also tolerates '|')
//   fixedAdvanceResponse <- stg_trigger_answer, used as-is. The sample value "optional
//                                                  (yes, I'm ready)" looks like descriptive
//                                                  placeholder text rather than a format
//                                                  Unity should parse (e.g. stripping the
//                                                  word "optional") - worth cleaning up in
//                                                  the real data if that's not intentional.
//   traineeActionCanAdvanceStage / advanceTriggerActionIds <- true / [act_id] whenever
//                                                  act_id is non-blank
//   cinematicId          <- stg.csc_file if present (see StageDto), else csc_id.ToString()
//                                                  as a fallback key into CinematicCatalog's
//                                                  local (Inspector-authored) entries, since
//                                                  there's no GET /api/cutscenes endpoint yet
//   sofiaPositionMarkerId <- stg.pos_name if present, else pos_id.ToString(), same reasoning
//                                                  (pos_name already encodes BOTH Sofia's
//                                                  position AND her pose/animation together -
//                                                  see PositionMarkerRegistry.Marker.animatorTrigger)
//
// The team plans to LEFT JOIN CUTSCENE/POSE into this endpoint server-side so csc_file/
// pos_name arrive directly instead of bare ids - StageDto already has both fields ready to
// receive them with no further Unity changes needed once that ships.
public class RemoteScenarioApiLoader : MonoBehaviour
{
    [SerializeField] private BackendConfig backendConfig;

    [Tooltip("Which scenario (scn_id) to load - development default is 1 (hardcoded, no " +
             "selection screen exists yet). A future scenario-selection screen should turn " +
             "Auto Start Loading off below, call SetScenarioId() with the trainee's choice, " +
             "then call BeginLoading() itself, rather than calling SetScenarioId() and hoping " +
             "it lands before this component's own Start() - Unity does not guarantee Start() " +
             "ordering between independent scripts, so racing it that way can silently load the " +
             "wrong (default) scenario depending on script execution order, which is exactly the " +
             "kind of thing that works in isolated testing and breaks the day a second script " +
             "exists. See autoStartLoading/BeginLoading() below.")]
    [SerializeField] private int scenarioId = 1;

    [Tooltip("On: this component loads scenarioId's data itself in Start() - today's " +
             "zero-calibration-screen default, unchanged from before this field existed. Off: " +
             "nothing loads until something else calls BeginLoading() explicitly - turn this " +
             "off once a real scenario-selection screen exists, so it can call SetScenarioId() " +
             "then BeginLoading() itself, in a guaranteed, race-free order, instead of relying " +
             "on script execution order to get SetScenarioId() in before this component's own " +
             "Start() fires.")]
    [SerializeField] private bool autoStartLoading = true;

    [SerializeField] private float timeoutSeconds = 10f;

    [Header("Wiring")]
    [SerializeField] private PatientScenarioController patientScenarioController;

    private bool hasStartedLoading = false;

    public void SetScenarioId(int id) => scenarioId = id;

    // Call this once, after SetScenarioId() if you're overriding the default, to actually
    // kick off the fetch - see autoStartLoading's tooltip for why a future scenario-selection
    // screen should call this explicitly (with autoStartLoading off) rather than relying on
    // Start() timing. Guarded against being called twice (e.g. once from Start()'s auto-start
    // and once explicitly) - LoadAndApply() firing concurrently twice would double-fetch and
    // race whichever result lands second into PatientScenarioController.
    public void BeginLoading()
    {
        if (hasStartedLoading)
        {
            Debug.LogWarning("[RemoteScenarioApiLoader] BeginLoading() called again after loading " +
                               "already started - ignoring. If you meant to load a DIFFERENT scenario, " +
                               "that's not supported mid-session yet (this loader assumes one scenario " +
                               "per scene load) - reload the scene instead.");
            return;
        }
        hasStartedLoading = true;
        StartCoroutine(LoadAndApply());
    }

    private void Awake()
    {
        // Must happen in Awake, not Start - see RemoteScenarioLoader's original comment on
        // this same requirement, still true here: Awake always runs before any Start(), so
        // this is guaranteed to land before SessionManager.Start() -> BeginScenario().
        patientScenarioController?.WaitForRemoteContent();
    }

    private void Start()
    {
        // See autoStartLoading's tooltip - a scenario-selection screen turns this off and
        // calls SetScenarioId() then BeginLoading() itself, in a guaranteed order, instead of
        // this racing that call against Unity's unordered Start() across independent scripts.
        if (autoStartLoading) BeginLoading();
    }

    private IEnumerator LoadAndApply()
    {
        if (patientScenarioController == null)
        {
            Debug.LogError("[RemoteScenarioApiLoader] No PatientScenarioController assigned - nothing to apply the loaded stages to.");
            yield break;
        }

        if (backendConfig == null || string.IsNullOrWhiteSpace(backendConfig.baseUrl))
        {
            Debug.LogWarning("[RemoteScenarioApiLoader] No BackendConfig/baseUrl assigned - falling back to the local PatientStageSequence asset.");
            patientScenarioController.UseFallbackStageSequence();
            yield break;
        }

        // Scenario-level persona (Patient Profile/Guardrail from the portal's "Edit Scenario"
        // form) fetched and applied BEFORE the stage sequence below - deliberately sequential,
        // not parallel, so the very first stage's SetStage()/BuildSystemPrompt() call already
        // sees the correct patient rather than racing it. Best-effort: unlike the stages fetch
        // below, a failure here logs a warning and continues rather than falling back to the
        // local PatientStageSequence asset - there's no reason a hiccup fetching WHO the patient
        // is should block the scenario from running at all, and sofiaPersona's own Inspector
        // patientName/medicalHistory already serve as a perfectly reasonable fallback persona.
        yield return FetchAndApplyScenarioPersona();

        string url = $"{backendConfig.baseUrl.TrimEnd('/')}/api/scenarios/{scenarioId}/stages";
        using UnityWebRequest request = UnityWebRequest.Get(url);
        request.timeout = Mathf.CeilToInt(timeoutSeconds);

        // SendWebRequest() itself can throw SYNCHRONOUSLY - not just fail asynchronously via
        // request.result - e.g. InvalidOperationException("Insecure connection not allowed")
        // when the URL is plain http:// and Player Settings' "Allow downloads over HTTP*" is
        // set to Not Allowed for this build target (this does NOT trigger in the Editor, only
        // in an actual built Player, which is exactly why this can pass every Editor test and
        // still fail the first time it runs from a real build). A try/catch can't wrap a
        // "yield return" directly (CS1626), so the call itself is isolated here and the
        // result is yielded separately below, outside the try block.
        UnityWebRequestAsyncOperation operation;
        try
        {
            operation = request.SendWebRequest();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RemoteScenarioApiLoader] SendWebRequest() threw for \"{url}\" ({ex.GetType().Name}: {ex.Message}) - " +
                             "if this is InvalidOperationException/\"Insecure connection not allowed\", the URL is plain http:// and " +
                             "this build's Player Settings > Other Settings > Configuration > \"Allow downloads over HTTP*\" is set " +
                             "to Not Allowed - set it to Always Allowed (or serve the backend over https://) rather than editing this " +
                             "script. Falling back to the local PatientStageSequence asset.");
            patientScenarioController.UseFallbackStageSequence();
            yield break;
        }

        yield return operation;

        if (request.result != UnityWebRequest.Result.Success)
        {
            // Covers the documented 404 "scenario doesn't exist" case too - a 404 still
            // surfaces as a failed result here, which is exactly what should fall back.
            Debug.LogError($"[RemoteScenarioApiLoader] Failed to fetch stages for scn_id={scenarioId} " +
                             $"({request.error}, HTTP {request.responseCode}) - falling back to the local PatientStageSequence asset.");
            patientScenarioController.UseFallbackStageSequence();
            yield break;
        }

        PatientStageDefinition[] stages;
        try
        {
            StageDto[] dtos = JsonArrayUtil.FromJsonArray<StageDto>(request.downloadHandler.text);
            stages = dtos.Select(ToStageDefinition).OrderBy(s => s.stageOrder).ToArray();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RemoteScenarioApiLoader] Failed to parse stages response ({ex.Message}) - falling back to the local PatientStageSequence asset.");
            patientScenarioController.UseFallbackStageSequence();
            yield break;
        }

        if (stages.Length == 0)
        {
            Debug.LogError("[RemoteScenarioApiLoader] Backend returned zero stages - falling back to the local PatientStageSequence asset.");
            patientScenarioController.UseFallbackStageSequence();
            yield break;
        }

        var sequence = ScriptableObject.CreateInstance<PatientStageSequence>();
        sequence.stages = stages;
        Debug.Log($"[RemoteScenarioApiLoader] Loaded {stages.Length} stage(s) for scn_id={scenarioId} from the backend.");
        patientScenarioController.SetStageSequence(sequence);
    }

    // Fetches GET /api/scenarios - the FULL LIST of scenarios (a bare JSON array, like /stages,
    // so JsonArrayUtil is needed here too), then picks out the one matching this component's
    // scenarioId. There is deliberately NO GET /api/scenarios/{id} single-item endpoint to call
    // instead: the team confirmed the real intended flow is that a future calibration/scenario-
    // selection screen fetches this same list ONCE to show the trainee their choices (every
    // scenario's Patient Profile/Guardrail/etc. all arrive together, from this one list call),
    // and only AFTER they pick one does anything fetch that scenario's /stages. Today, with that
    // selection screen still unbuilt (SessionManager.ScenarioSelection) and scenarioId instead
    // just an Inspector default standing in for "the trainee's choice," fetching the list here
    // and filtering client-side is the correct stand-in - and remains exactly correct later too,
    // it just becomes slightly redundant with whatever fetch the selection screen already did to
    // build its picker. FOLLOW-UP once that screen exists: consider having it hand its
    // already-fetched ScenarioDto straight to PatientScenarioController.ApplyScenarioPersona()
    // (or a new SetSelectedScenario() on this class) instead of this method re-fetching the same
    // list a second time - not worth building against a screen that doesn't exist yet, but worth
    // remembering as the natural next simplification once it does.
    //
    // Applies scn_patient_profile/scn_guardrail to Sofia via PatientScenarioController - this is
    // what lets a tutor change which patient a scenario uses entirely from the portal's "Edit
    // Scenario" form, no Unity rebuild. scn_name/scn_description aren't consumed here: scn_name
    // is scenario-selection-screen-facing (not yet built), and scn_description is trainee-facing
    // framing text with nowhere to display yet either - both harmless to leave unused for now.
    //
    // Deliberately its own small try/catch'd fetch, separate from the /stages one below, rather
    // than trying to combine them into one request - this endpoint's failure mode is "keep going
    // with a warning" (see call site comment), while /stages' is "fall back to the local
    // PatientStageSequence asset entirely" - conflating the two would force one failure-handling
    // policy onto both, when they actually need different ones.
    private IEnumerator FetchAndApplyScenarioPersona()
    {
        ScenarioDto[] scenarios = null;
        string fetchError = null;
        yield return FetchScenarioList(backendConfig, timeoutSeconds,
            onSuccess: list => scenarios = list,
            onError: err => fetchError = err);

        if (scenarios == null)
        {
            Debug.LogWarning($"[RemoteScenarioApiLoader] {fetchError} - continuing with Sofia's Inspector-configured " +
                               "patientName/medicalHistory as a fallback persona.");
            yield break;
        }

        ScenarioDto dto = scenarios.FirstOrDefault(s => s.scn_id == scenarioId);
        if (dto == null)
        {
            Debug.LogWarning($"[RemoteScenarioApiLoader] Backend returned {scenarios.Length} scenario(s) but none matched " +
                               $"scn_id={scenarioId} - continuing with Sofia's Inspector-configured patientName/medicalHistory " +
                               "as a fallback persona.");
            yield break;
        }

        patientScenarioController.ApplyScenarioPersona(dto.scn_patient_profile, dto.scn_guardrail);
        Debug.Log($"[RemoteScenarioApiLoader] Applied scenario persona for scn_id={scenarioId} (\"{dto.scn_name}\") from the backend.");
    }

    // Shared GET /api/scenarios fetch+parse - extracted out so FetchAndApplyScenarioPersona()
    // above and ScenarioSelectionController (the Welcome/Scenario Selection UI, built once
    // that screen actually existed - see project doc section 46) share one implementation
    // instead of two copies that could quietly drift apart, the same reasoning that already
    // pulled CsvUtil.ParseCsv() out for RemoteScenarioLoader/RemoteCinematicCatalogLoader to
    // share. Static and instance-independent on purpose - a UI controller populating a
    // dropdown has no reason to need a scenarioId, a PatientScenarioController reference, or
    // any of this component's other per-scenario state, just the raw list.
    //
    // Invokes exactly one of onSuccess/onError, never both, never neither - callers can treat
    // "did onSuccess fire" as the complete success/failure signal without checking anything
    // else. onError receives a plain, already-formatted message (no "[ClassName]" prefix or
    // Debug.Log call baked in) so each caller can log/display it however fits its own context
    // (a fallback-persona warning here, a descriptionField error message in the UI there).
    public static IEnumerator FetchScenarioList(BackendConfig backendConfig, float timeoutSeconds,
                                                 Action<ScenarioDto[]> onSuccess, Action<string> onError)
    {
        if (backendConfig == null || string.IsNullOrWhiteSpace(backendConfig.baseUrl))
        {
            onError?.Invoke("No BackendConfig/baseUrl assigned");
            yield break;
        }

        string url = $"{backendConfig.baseUrl.TrimEnd('/')}/api/scenarios";
        using UnityWebRequest request = UnityWebRequest.Get(url);
        request.timeout = Mathf.CeilToInt(timeoutSeconds);

        // Same "SendWebRequest() can throw synchronously" guard as LoadAndApply() above -
        // see that method's comment for the full explanation (insecure-connection check in a
        // built Player, try/catch can't wrap a yield return directly).
        UnityWebRequestAsyncOperation operation;
        try
        {
            operation = request.SendWebRequest();
        }
        catch (Exception ex)
        {
            onError?.Invoke($"SendWebRequest() threw for \"{url}\" ({ex.GetType().Name}: {ex.Message})");
            yield break;
        }

        yield return operation;

        if (request.result != UnityWebRequest.Result.Success)
        {
            onError?.Invoke($"Failed to fetch the scenario list ({request.error}, HTTP {request.responseCode})");
            yield break;
        }

        ScenarioDto[] scenarios;
        try
        {
            scenarios = JsonArrayUtil.FromJsonArray<ScenarioDto>(request.downloadHandler.text);
        }
        catch (Exception ex)
        {
            onError?.Invoke($"Failed to parse the scenario list response ({ex.Message})");
            yield break;
        }

        onSuccess?.Invoke(scenarios);
    }

    private static PatientStageDefinition ToStageDefinition(StageDto dto)
    {
        var stage = ScriptableObject.CreateInstance<PatientStageDefinition>();

        stage.stageId = dto.stg_id.ToString();
        stage.stageOrder = dto.stg_order;
        stage.scenarioStage = dto.stg_prompt ?? "";
        stage.currentVitals = dto.stg_vital ?? "";
        stage.stageGuardrail = dto.stg_guardrail ?? "";
        stage.narrationForTrainee = ""; // no matching column yet - see class comment

        stage.stageHint = dto.stg_bot_hint ?? "";
        stage.advanceConfirmationHandledByBot = (dto.stg_trigger ?? "").ToLowerInvariant().Contains("bot");

        bool hasPhrase = !string.IsNullOrWhiteSpace(dto.stg_trigger_phrase) || !string.IsNullOrWhiteSpace(dto.stg_trigger_answer);
        stage.conversationCanAdvanceStage = hasPhrase;
        stage.advanceTriggerPhrases = SplitList(dto.stg_trigger_phrase);
        stage.fixedAdvanceResponse = dto.stg_trigger_answer ?? "";

        // advanceCondition used to have no matching column and was always left blank - which
        // meant SofiaPersona.BuildSystemPrompt() never appended its [STAGE_ADVANCE]/
        // [STAGE_CONTINUE] instruction block at all, so ContainsAdvanceTriggerPhrase()'s literal
        // substring check was the ONLY way any backend-driven stage could ever advance. That's
        // confirmed too rigid for real trainee speech (e.g. "ready for treatment" not matching
        // an authored "are you ready" phrase). Synthesizing a plain-language condition from the
        // SAME stg_trigger_phrase data - rather than adding a new backend column - switches this
        // back on: the LLM tag path becomes a second, additive way to advance, OR'd with (never
        // instead of) the still-active exact-phrase check in SofiaPersona.HandleResponseReceived
        // (`pendingPhraseAdvance || tagRequestsAdvance`). Worst case if the LLM doesn't emit the
        // tag on some paraphrase, behaviour is identical to today - the authored phrases still
        // catch it deterministically. This does reintroduce the known LLM-tag reliability caveat
        // from PatientStageDefinition.advanceCondition's own doc comment ("NOT reliably emitted
        // every time... a real limit of LLM instruction-following") - watch the Console for
        // "[SofiaPersona] Expected a stage-advance tag but found neither..." during testing to
        // see how often it actually fires. Only bot-owned confirmations (advanceConfirmationHandledByBot)
        // don't benefit from this yet - PatientScenarioController.ApplyCurrentStage() doesn't
        // currently pass advanceCondition through to VirtualAssistantPersona.SetStage() at all -
        // worth a follow-up if a bot-confirmed stage ever needs the same flexibility.
        stage.advanceCondition = hasPhrase
            ? "The trainee's utterance conveys essentially the same request/agreement as one of " +
              $"these example phrasings for THIS stage, even if worded quite differently: " +
              $"{string.Join("; ", stage.advanceTriggerPhrases)}. Judge by meaning and context, not " +
              "exact wording - e.g. \"ready for the treatment\" counts just as much as an authored " +
              "\"are you ready\" example. If genuinely ambiguous whether the trainee meant this, " +
              "output [STAGE_CONTINUE] rather than guessing."
            : "";

        bool hasAction = !string.IsNullOrWhiteSpace(dto.act_id);
        stage.traineeActionCanAdvanceStage = hasAction;
        stage.advanceTriggerActionIds = hasAction ? new[] { dto.act_id.Trim() } : Array.Empty<string>();

        stage.cinematicId = !string.IsNullOrWhiteSpace(dto.csc_file)
            ? dto.csc_file.Trim()
            : (dto.csc_id > 0 ? dto.csc_id.ToString() : "");

        stage.sofiaPositionMarkerId = !string.IsNullOrWhiteSpace(dto.pos_name)
            ? dto.pos_name.Trim()
            : (dto.pos_id > 0 ? dto.pos_id.ToString() : "");

        return stage;
    }

    private static string[] SplitList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        return raw.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(p => p.Trim())
                   .Where(p => p.Length > 0)
                   .ToArray();
    }
}
