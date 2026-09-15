using UnityEngine;

// Owns progression through a scenario's patient-condition stages during the
// SessionManager.ScenarioActive state (section 9). Deliberately does NOT decide
// on its own when to advance - per the project's established rule (learned the
// hard way from the Convai Actions bug and repeated in the readiness-check design):
// never let something this consequential be gated by free-form LLM output alone.
// AdvanceStage() is a public method meant to be called from a DETERMINISTIC source -
// today that's most likely a debug key or a "Continue" UI button; later it should be
// called from the grading API's response (next_action == "advance", per section 14
// of the project doc) once that backend exists, or from a proximity trigger the same
// way Convai's Narrative Design sections advance via InvokeSelectedTrigger().
public class PatientScenarioController : MonoBehaviour
{
    // So any interactable prop's InteractableActions (right-click action menu, see
    // ActionMenuController) can reach the one scenario controller in the scene without
    // needing an Inspector-dragged reference each time - same reasoning as
    // SessionManager.Instance. Not DontDestroyOnLoad-persisted like that one, since this
    // is scoped to a single scenario run, not the whole app session.
    public static PatientScenarioController Instance { get; private set; }

    [SerializeField] private SofiaPersona sofiaPersona;

    [Tooltip("Optional. A stage whose advanceConfirmationHandledByBot is true routes its " +
             "conversational advance-trigger phrase/response to THIS character instead of Sofia - " +
             "e.g. the trainee tells the bot \"I'm ready\" during a readiness check. Also always " +
             "receives each stage's stageHint, independent of who handles confirmation. Leave " +
             "unassigned if the scenario has no Virtual Assistant character yet.")]
    [SerializeField] private VirtualAssistantPersona virtualAssistantPersona;

    [SerializeField] private PatientStageSequence stageSequence;

    [Header("Stage-transition presentation (all optional)")]
    [Tooltip("Plays a stage's cinematicId (if any) before that stage is applied. Leave unassigned " +
             "to skip cutscenes entirely, even for stages that have a cinematicId set.")]
    [SerializeField] private CinematicController cinematicController;

    [Tooltip("Animator that drives Sofia's pose/animation - target of each stage's sofiaPoseTrigger.")]
    [SerializeField] private Animator sofiaAnimator;

    [Tooltip("Transform to teleport for each stage's sofiaPositionMarkerId. Leave unassigned to " +
             "default to sofiaPersona's own transform.")]
    [SerializeField] private Transform sofiaTransform;

    [Tooltip("Named scene positions (door, chair seated, chair reclined, ...) that a stage's " +
             "sofiaPositionMarkerId looks up. Leave unassigned to skip position changes entirely, " +
             "even for stages that have a sofiaPositionMarkerId set.")]
    [SerializeField] private PositionMarkerRegistry positionMarkerRegistry;

    [Header("Temporary MVP advance trigger (replace once grading API exists)")]
    [SerializeField] private bool allowDebugAdvanceKey = true;
    [SerializeField] private UnityEngine.InputSystem.Key debugAdvanceKey = UnityEngine.InputSystem.Key.N;

    private int currentStageIndex = -1;
    private bool subscribedToSofia = false;
    private bool subscribedToVirtualAssistant = false;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[PatientScenarioController] Another instance already exists in this scene - destroying this duplicate.");
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    // Two-gate start: the scenario only actually begins once BOTH SessionManager has asked
    // for it (sessionRequestedStart, via BeginScenario()) AND the stage content itself is
    // ready (stagesReady). Defaults to true so nothing changes for a scene that just assigns
    // stageSequence in the Inspector, same as before this existed. A RemoteScenarioLoader in
    // the scene flips stagesReady false at its own Awake() (see WaitForRemoteContent()) -
    // Awake always runs before any Start(), so this is guaranteed to happen before
    // SessionManager.Start() gets a chance to call BeginScenario() - and flips it back once
    // the tutor-authored spreadsheet has been fetched and parsed, or a fallback kicks in.
    private bool sessionRequestedStart = false;
    private bool stagesReady = true;

    public string CurrentStageId =>
        (stageSequence != null && currentStageIndex >= 0 && currentStageIndex < stageSequence.stages.Length)
            ? stageSequence.stages[currentStageIndex].stageId
            : null;

    // Called by a RemoteScenarioLoader's own Awake(), if one is present in the scene, to hold
    // BeginScenario() off until the remote spreadsheet has actually been fetched.
    public void WaitForRemoteContent()
    {
        stagesReady = false;
    }

    // Called by RemoteScenarioLoader once it has successfully fetched and parsed the tutor's
    // spreadsheet - replaces stageSequence with the freshly-built one and unblocks the start.
    public void SetStageSequence(PatientStageSequence sequence)
    {
        stageSequence = sequence;
        stagesReady = true;
        TryStartIfReady();
    }

    // Called by RemoteScenarioApiLoader once it has fetched GET /api/scenarios/{id} - the
    // SCENARIO-level row a tutor edits in the portal's "Edit Scenario" form (Patient Profile,
    // Guardrail), as opposed to SetStageSequence() above which handles the per-STAGE rows.
    // Forwarded straight to sofiaPersona rather than having the loader reach into it directly -
    // keeps PatientScenarioController as the one hub everything else talks to, consistent with
    // how ReportTraineeAction/CurrentStageId already work. Deliberately does NOT gate
    // stagesReady/TryStartIfReady the way SetStageSequence() does - a missing or failed
    // scenario-persona fetch should degrade gracefully to sofiaPersona's own Inspector
    // patientName/medicalHistory defaults, not block the whole scenario from starting the way a
    // missing STAGE sequence genuinely would (there's no sensible fallback for "what stages
    // exist", but "what patient is this" already has one).
    public void ApplyScenarioPersona(string patientProfile, string scenarioGuardrail)
    {
        if (sofiaPersona == null)
        {
            Debug.LogWarning("[PatientScenarioController] ApplyScenarioPersona() called but no Sofia Persona is assigned - ignoring. " +
                               "Check this component's \"Sofia Persona\" field in the Inspector - and if it looks correctly " +
                               "assigned, check for a second PatientScenarioController in the scene that RemoteScenarioApiLoader's " +
                               "\"Patient Scenario Controller\" field might actually be pointing at instead.");
            return;
        }
        sofiaPersona.ApplyScenarioPersona(patientProfile, scenarioGuardrail);
    }

    // Called by RemoteScenarioLoader when there's no URL configured, or the fetch/parse
    // failed - keeps whatever stageSequence is already assigned in the Inspector (if any)
    // and unblocks the start with that as a fallback, rather than leaving the scenario
    // stuck waiting forever over something like a flaky headset wifi connection.
    public void UseFallbackStageSequence()
    {
        stagesReady = true;
        TryStartIfReady();
    }

    // Call this once when SessionManager enters ScenarioActive.
    public void BeginScenario()
    {
        sessionRequestedStart = true;
        TryStartIfReady();
    }

    private void TryStartIfReady()
    {
        if (!sessionRequestedStart || !stagesReady) return;

        if (stageSequence == null || stageSequence.stages == null || stageSequence.stages.Length == 0)
        {
            Debug.LogError("[PatientScenarioController] No stage sequence assigned - cannot begin scenario.");
            return;
        }

        // sofiaPersona is required - every stage transition drives her (SetStage() at minimum),
        // so there is no meaningful "start without her" mode. This used to be an unguarded
        // sofiaPersona.OnAdvanceIntentDetected += ... below, which threw an unhandled
        // NullReferenceException the instant this ran if Sofia Persona was left unassigned in
        // the Inspector (or pointed at the wrong PatientScenarioController if more than one
        // exists in the scene - check the Console for "Another instance already exists in this
        // scene - destroying this duplicate" if this fires unexpectedly) - which silently killed
        // this coroutine/call chain with no indication of WHY. Failing loudly and returning here
        // instead turns that into an actionable message and leaves the rest of the app running.
        if (sofiaPersona == null)
        {
            Debug.LogError("[PatientScenarioController] No Sofia Persona assigned - cannot begin scenario. " +
                             "Check this component's \"Sofia Persona\" field in the Inspector, and check the " +
                             "Console for \"Another instance already exists in this scene\" in case a duplicate " +
                             "PatientScenarioController is the one actually wired up elsewhere (e.g. on " +
                             "RemoteScenarioApiLoader's own \"Patient Scenario Controller\" field).");
            return;
        }

        if (!subscribedToSofia)
        {
            sofiaPersona.OnAdvanceIntentDetected += HandleConversationalAdvanceIntent;
            subscribedToSofia = true;
        }

        if (!subscribedToVirtualAssistant && virtualAssistantPersona != null)
        {
            virtualAssistantPersona.OnAdvanceIntentDetected += HandleConversationalAdvanceIntent;
            subscribedToVirtualAssistant = true;
        }

        currentStageIndex = 0;
        EnterCurrentStage();
    }

    private void OnDestroy()
    {
        if (subscribedToSofia && sofiaPersona != null)
            sofiaPersona.OnAdvanceIntentDetected -= HandleConversationalAdvanceIntent;

        if (subscribedToVirtualAssistant && virtualAssistantPersona != null)
            virtualAssistantPersona.OnAdvanceIntentDetected -= HandleConversationalAdvanceIntent;

        if (Instance == this) Instance = null;
    }

    // Shared handler for BOTH conversational advance sources: Sofia's reply carrying
    // [STAGE_ADVANCE]/a matched phrase, OR the Virtual Assistant bot matching its
    // confirmation trigger phrase (see advanceConfirmationHandledByBot) - either one just
    // means "the conversational-advance condition for the CURRENT stage was met",
    // regardless of which character the trainee was actually talking to. Still goes
    // through the same deterministic AdvanceStage() path as the debug key - this is just a
    // different trigger source, not a separate/looser advancement rule. Also re-checks the
    // CURRENT stage's own flag as defense in depth: if the stage that was active when this
    // fired doesn't actually allow conversational advancement (e.g. a race during a stage
    // transition), this refuses to act on it.
    private void HandleConversationalAdvanceIntent()
    {
        var stage = GetCurrentStage();
        if (stage == null || !stage.conversationCanAdvanceStage)
        {
            Debug.LogWarning("[PatientScenarioController] Ignoring advance intent - current stage doesn't allow conversational advancement.");
            return;
        }

        AdvanceStage();
    }

    // Called by an InteractableActions (on a syringe, a chair lever, etc.) once the trainee
    // right-clicks it, picks an option from the popup action menu, and that option is
    // confirmed to belong to that object - trigger path (b) from the transition-flow spec,
    // parallel to HandleConversationalAdvanceIntent's path (a). Goes through the exact same
    // deterministic AdvanceStage() as every other trigger source; only the detection differs.
    public void ReportTraineeAction(string actionId)
    {
        var stage = GetCurrentStage();
        if (stage == null || !stage.traineeActionCanAdvanceStage)
        {
            Debug.LogWarning($"[PatientScenarioController] Ignoring trainee action \"{actionId}\" - current stage doesn't allow action-based advancement.");
            return;
        }

        if (!ContainsActionId(stage.advanceTriggerActionIds, actionId))
        {
            Debug.Log($"[PatientScenarioController] Trainee action \"{actionId}\" doesn't match this stage's expected action id(s) - ignoring.");
            return;
        }

        Debug.Log($"[PatientScenarioController] Advancing stage from trainee action \"{actionId}\".");
        AdvanceStage();
    }

    private static bool ContainsActionId(string[] actionIds, string actionId)
    {
        if (actionIds == null || string.IsNullOrWhiteSpace(actionId)) return false;

        foreach (string id in actionIds)
            if (string.Equals(id?.Trim(), actionId.Trim(), System.StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private PatientStageDefinition GetCurrentStage()
    {
        if (stageSequence == null || currentStageIndex < 0 || currentStageIndex >= stageSequence.stages.Length)
            return null;
        return stageSequence.stages[currentStageIndex];
    }

    // Call this whenever the deterministic advance condition fires (correct answer
    // graded, trigger collider entered, debrief-ready signal, etc.) - never from
    // parsing the patient's own free-form dialogue.
    //
    // Full transition flow per stage (see project doc): 1) this method is the "trigger"
    // step - it's already been called from a deterministic source (conversation intent,
    // a trainee action, the debug key). 2) if the NEW stage has a cinematicId, play that
    // letterboxed cutscene first, with input disabled, before anything else happens.
    // 3) once the cutscene finishes (or immediately, if there wasn't one), apply the new
    // stage: Sofia's pose/animation and/or position first (steps 2-3 of the spec are
    // presentation, so they land before her dialogue persona switches), then her
    // conversation persona as before.
    public void AdvanceStage()
    {
        if (stageSequence == null) return;

        currentStageIndex++;

        if (currentStageIndex >= stageSequence.stages.Length)
        {
            Debug.Log("[PatientScenarioController] Final stage complete - handing off to Debrief.");
            if (SessionManager.Instance != null) SessionManager.Instance.AdvanceState();
            return;
        }

        EnterCurrentStage();
    }

    // Shared by both the very first stage (from TryStartIfReady) and every subsequent
    // advance: play the incoming stage's cutscene, if it has one, before applying it -
    // so an intro cutscene works identically to a mid-scenario one, with no special-casing
    // needed for "the first stage" versus any other.
    private void EnterCurrentStage()
    {
        var stage = stageSequence.stages[currentStageIndex];

        if (!string.IsNullOrWhiteSpace(stage.cinematicId) && cinematicController != null)
            cinematicController.Play(stage.cinematicId, ApplyCurrentStage);
        else
            ApplyCurrentStage();
    }

    private void ApplyCurrentStage()
    {
        var stage = stageSequence.stages[currentStageIndex];
        Debug.Log($"[PatientScenarioController] Entering stage \"{stage.stageId}\" ({stage.stageOrder}).");

        ApplySofiaPoseAndPosition(stage);

        // The stage's conversational advance-trigger fields (conversationCanAdvanceStage,
        // advanceTriggerPhrases, fixedAdvanceResponse) belong to exactly ONE of Sofia or the
        // bot per stage, never both - advanceConfirmationHandledByBot says which. Whichever
        // one DOESN'T own it for this stage gets conversationCanAdvanceStage=false and no
        // phrases/response at all, so it can't also independently fire an advance from data
        // that was actually meant for the other character.
        bool botHandlesConfirmation = stage.advanceConfirmationHandledByBot;

        sofiaPersona.SetStage(stage.scenarioStage, stage.currentVitals, stage.stageGuardrail,
                               !botHandlesConfirmation && stage.conversationCanAdvanceStage, stage.advanceCondition,
                               !botHandlesConfirmation ? stage.advanceTriggerPhrases : System.Array.Empty<string>(),
                               !botHandlesConfirmation ? stage.fixedAdvanceResponse : "");

        // The bot always gets this stage's hint seed content (independent of who handles
        // confirmation - asking it for help should work regardless; it feeds the bot's own
        // LlmAgent system prompt, see VirtualAssistantPersona), plus the confirmation
        // phrase/response ONLY when this stage says it owns that role. The bot's LLM never
        // decides advancement either way - only the deterministic phrase match does.
        virtualAssistantPersona?.SetStage(stage.stageHint,
                               botHandlesConfirmation && stage.conversationCanAdvanceStage,
                               botHandlesConfirmation ? stage.advanceTriggerPhrases : System.Array.Empty<string>(),
                               botHandlesConfirmation ? stage.fixedAdvanceResponse : "");

        // If you have a narrator/caption UI, surface stage.narrationForTrainee here too -
        // it's separate from Sofia's own dialogue on purpose (matches the Convai setup
        // doc's pattern of narration text being distinct from what the patient character says).
    }

    // Step 3 of the transition flow: change Sofia's pose/animation and/or teleport her to a
    // named scene position for the stage that's about to begin. Typically only meaningful
    // paired with a cinematicId on the same stage, so the teleport/pose-snap happens hidden
    // behind the cutscene rather than visibly popping in front of the trainee - but nothing
    // here requires that; a stage can set these with no cinematicId if an instant change is
    // actually what you want (e.g. a pose tweak too subtle to need its own cutscene).
    private void ApplySofiaPoseAndPosition(PatientStageDefinition stage)
    {
        // A stage's own explicit sofiaPoseTrigger (hand-authored ScriptableObject stages
        // only - the backend never sets this field, see RemoteScenarioApiLoader) always
        // takes priority when set, for a pose change with no accompanying move. Otherwise,
        // if the resolved position marker itself carries a paired animatorTrigger (the
        // usual case for backend-driven stages, since POSE is one combined "where + what
        // pose" id there), that fires instead - handled together below once the marker's
        // been looked up.
        bool explicitPoseFired = false;
        if (!string.IsNullOrWhiteSpace(stage.sofiaPoseTrigger))
        {
            if (sofiaAnimator != null)
            {
                sofiaAnimator.SetTrigger(stage.sofiaPoseTrigger);
                explicitPoseFired = true;
            }
            else
            {
                Debug.LogWarning($"[PatientScenarioController] Stage \"{stage.stageId}\" has a sofiaPoseTrigger " +
                                   "but no Sofia Animator is assigned - skipping.");
            }
        }

        if (!string.IsNullOrWhiteSpace(stage.sofiaPositionMarkerId))
        {
            if (positionMarkerRegistry == null)
            {
                Debug.LogWarning($"[PatientScenarioController] Stage \"{stage.stageId}\" has a sofiaPositionMarkerId " +
                                   "but no PositionMarkerRegistry is assigned - skipping.");
            }
            else if (!positionMarkerRegistry.TryGet(stage.sofiaPositionMarkerId, out Transform marker, out string markerTrigger))
            {
                Debug.LogWarning($"[PatientScenarioController] Stage \"{stage.stageId}\" wants position marker " +
                                   $"\"{stage.sofiaPositionMarkerId}\" but no marker with that id was found.");
            }
            else
            {
                Transform target = sofiaTransform != null ? sofiaTransform : sofiaPersona.transform;
                target.SetPositionAndRotation(marker.position, marker.rotation);

                // If Sofia's rig has a LookAtPlayer with lockPosition on (anywhere in her
                // hierarchy - not necessarily on this exact "target" GameObject), it re-asserts
                // its OWN cached position every LateUpdate() specifically to resist exactly
                // this kind of external move - confirmed the cause of "position sets correctly,
                // then silently reverts" during real testing. Telling it about the new position
                // (and, as of the lockRotation addition, the new rotation too) keeps that
                // protection intact for anything else while no longer fighting this legitimate,
                // deliberate teleport.
                //
                // Deliberately searches children AND parents, not just target itself -
                // TryGetComponent (the original version of this check) only looks at the exact
                // "target" GameObject, with no traversal at all. If LookAtPlayer actually lives
                // on a sibling/child mesh object, or on a parent rig root, one GameObject away
                // from whatever "target" resolves to (the Sofia Transform field above, or
                // sofiaPersona's own transform if that's unassigned), TryGetComponent silently
                // finds nothing, SetLockedPosition() never fires, and LookAtPlayer's lockPosition
                // fights this teleport right back to wherever it was at Start() - this was
                // confirmed as the cause of chair_seated position/rotation "mess" that only
                // appeared after LookAtPlayer was added to Sofia's hierarchy (it worked fine with
                // the exact same marker + sit animation before LookAtPlayer existed).
                var lookAtPlayer = target.GetComponentInChildren<LookAtPlayer>();
                if (lookAtPlayer == null)
                    lookAtPlayer = target.GetComponentInParent<LookAtPlayer>();

                if (lookAtPlayer != null)
                {
                    lookAtPlayer.SetLockedPosition(marker.position);

                    // Same reasoning as SetLockedPosition above, but for LookAtPlayer's own
                    // lockRotation hold - without this, a marker that teleports Sofia into a
                    // non-"standing" pose (chair_seated, laying_unconscious, etc.) would have its
                    // rotation immediately overwritten by lockRotation snapping back to whatever
                    // rotation was locked in before this transition, on the very next LateUpdate().
                    lookAtPlayer.SetLockedRotation(marker.rotation);

                    if (lookAtPlayer.transform != target)
                    {
                        Debug.LogWarning($"[PatientScenarioController] LookAtPlayer found on \"{lookAtPlayer.name}\", " +
                                           $"which is NOT the same GameObject as \"{target.name}\" (this stage's teleport " +
                                           "target) - this used to be silently missed entirely before this warning was " +
                                           "added. Its locked position AND rotation have been synced this time, but it's " +
                                           "worth moving either LookAtPlayer or double-checking the Sofia Transform field " +
                                           "so both always agree on the same object going forward.");
                    }
                }

                // TEMP diagnostic - safe to delete once the "still standing at the door" bug is
                // confirmed fixed. Reports exactly what this call did: which GameObject actually
                // got moved, where it was told to go, and where it reads back as being one frame
                // later - the last part specifically catches a NavMeshAgent (or anything else
                // that owns this Transform every frame) silently overriding the manual teleport,
                // which SetPositionAndRotation alone can't detect or prevent.
                Debug.Log($"[PatientScenarioController] Position marker \"{stage.sofiaPositionMarkerId}\" resolved - " +
                           $"moving \"{target.name}\" to {marker.position} (rotation {marker.rotation.eulerAngles}). " +
                           $"Immediately after the call, target.position reads back as {target.position}.");
                if (target.TryGetComponent<UnityEngine.AI.NavMeshAgent>(out var agent))
                {
                    Debug.LogWarning($"[PatientScenarioController] \"{target.name}\" also has a NavMeshAgent - if it stays at " +
                                       "the old position despite the log above, the agent is overriding this manual " +
                                       "transform set every frame. Fix: call agent.Warp(marker.position) instead of/alongside " +
                                       "SetPositionAndRotation, or disable the agent's \"Update Position\"/\"Update Rotation\" " +
                                       "if it isn't actually doing pathfinding.");
                }

                if (!explicitPoseFired && !string.IsNullOrWhiteSpace(markerTrigger))
                {
                    if (sofiaAnimator == null)
                    {
                        Debug.LogWarning($"[PatientScenarioController] Position marker \"{stage.sofiaPositionMarkerId}\" " +
                                           "has an animatorTrigger but no Sofia Animator is assigned - skipping.");
                    }
                    else
                    {
                        // Animator.SetTrigger(name) matches against a TRIGGER PARAMETER (Animator
                        // window's Parameters tab), not a Layer, State, or anything else that can
                        // share the same name - confirmed the cause of "transition triggered
                        // (position moved fine) but animation never changes" during real testing,
                        // where markerTrigger's name existed as a LAYER in the Animator Controller
                        // instead of a Trigger parameter. SetTrigger() does not throw or return
                        // anything when the name doesn't match any parameter - it just silently
                        // does nothing - so this checks first and reports exactly what's wrong
                        // instead of leaving that failure invisible.
                        bool triggerParamExists = false;
                        foreach (var param in sofiaAnimator.parameters)
                        {
                            if (param.type == AnimatorControllerParameterType.Trigger && param.name == markerTrigger)
                            {
                                triggerParamExists = true;
                                break;
                            }
                        }

                        if (!triggerParamExists)
                        {
                            Debug.LogWarning($"[PatientScenarioController] Position marker \"{stage.sofiaPositionMarkerId}\"'s " +
                                               $"animatorTrigger \"{markerTrigger}\" does not match any TRIGGER PARAMETER on " +
                                               $"\"{sofiaAnimator.name}\"'s Animator Controller - SetTrigger() just silently did " +
                                               "nothing. Check the Animator window's PARAMETERS tab (not the Layers tab) for a " +
                                               $"Trigger named exactly \"{markerTrigger}\" - a Layer or State with a matching " +
                                               "name looks similar in the window but has no effect on Animator.SetTrigger() at all.");
                        }
                        else
                        {
                            sofiaAnimator.SetTrigger(markerTrigger);
                            Debug.Log($"[PatientScenarioController] Fired Animator trigger \"{markerTrigger}\" on \"{sofiaAnimator.name}\". " +
                                       "If the visible animation still doesn't change, check that a transition actually exists " +
                                       "FROM the current state (or from Any State) TO the target state, with this trigger set " +
                                       "as that transition's Condition - a trigger with no transition consuming it does nothing " +
                                       "even when the parameter itself is real and correctly set.");
                        }
                    }
                }
            }
        }
    }

    private void Update()
    {
        if (!allowDebugAdvanceKey) return;
        if (UnityEngine.InputSystem.Keyboard.current == null) return;

        if (UnityEngine.InputSystem.Keyboard.current[debugAdvanceKey].wasPressedThisFrame)
        {
            AdvanceStage();
        }
    }
}
