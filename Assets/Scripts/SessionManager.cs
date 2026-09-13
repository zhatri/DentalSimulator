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
