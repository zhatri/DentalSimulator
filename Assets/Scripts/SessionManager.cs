using UnityEngine;

// The single state machine that owns the whole session's flow (section 9 of the
// project doc): ScenarioSelection -> Calibration -> ReadinessCheck -> ScenarioActive
// -> Debrief. Nothing else should decide on its own when to move between these -
// everything else (calibration UI, the readiness-check parser, PatientScenarioController)
// just calls AdvanceState() when ITS job is done, and SessionManager decides what
// happens next.
public class SessionManager : MonoBehaviour
{
    // Singleton so any script (PatientScenarioController, a future calibration script,
    // a future readiness-check script) can reach it with SessionManager.Instance,
    // without every script needing an Inspector-dragged reference to it.
    public static SessionManager Instance { get; private set; }

    public enum SessionState
    {
        ScenarioSelection,
        Calibration,
        ReadinessCheck,
        ScenarioActive,
        Debrief
    }

    [Header("Where the session starts. See setup notes - while you're testing Sofia in " +
            "isolation (no selection/calibration UI built yet), set this to ScenarioActive.")]
    [SerializeField] private SessionState startingState = SessionState.ScenarioSelection;

    [Header("Wire this to the PatientScenarioController in your scene")]
    [SerializeField] private PatientScenarioController patientScenarioController;

    public SessionState CurrentState { get; private set; }
    public event System.Action<SessionState> OnStateChanged;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[SessionManager] Another instance already exists - destroying this duplicate.");
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        EnterState(startingState);
    }

    // Call this from whatever just finished its job: a "scenario chosen" button, the
    // calibration confirm step, the readiness-check parser on [READY_CONFIRMED],
    // or PatientScenarioController once every patient stage is done.
    public void AdvanceState()
    {
        switch (CurrentState)
        {
            case SessionState.ScenarioSelection: EnterState(SessionState.Calibration); break;
            case SessionState.Calibration: EnterState(SessionState.ReadinessCheck); break;
            case SessionState.ReadinessCheck: EnterState(SessionState.ScenarioActive); break;
            case SessionState.ScenarioActive: EnterState(SessionState.Debrief); break;
            case SessionState.Debrief:
                Debug.Log("[SessionManager] Already at Debrief - nothing further to advance to.");
                break;
        }
    }

    // Jumps straight to ScenarioActive, bypassing Calibration/ReadinessCheck entirely - call
    // this from the Scenario Selection screen's own Start button (see
    // ScenarioSelectionController/WelcomeScreenController) rather than calling AdvanceState()
    // three times in a row to walk through the intermediate states. Those two states were
    // designed (project doc section 9) around a room-scale physical walk-to-a-mark
    // calibration and a separate bot-mediated readiness check - both superseded once the
    // deployment was confirmed as seated/classroom (section 17) and the team instead built a
    // single Welcome -> Scenario Selection UI flow that already covers "pick a scenario, then
    // explicitly commit to starting" without needing either intermediate state. Left as a
    // separate method rather than changing what AdvanceState() does for ScenarioSelection, so
    // a scene that still wants the original step-by-step flow (e.g. testing a future real
    // calibration/readiness screen) isn't affected.
    public void SkipToScenarioActive() => EnterState(SessionState.ScenarioActive);

    private void EnterState(SessionState state)
    {
        CurrentState = state;
        Debug.Log($"[SessionManager] Entering state: {state}");

        if (state == SessionState.ScenarioActive)
        {
            if (patientScenarioController != null)
                patientScenarioController.BeginScenario();
            else
                Debug.LogWarning("[SessionManager] No PatientScenarioController assigned - the patient scenario won't start.");
        }

        OnStateChanged?.Invoke(state);
    }
}
