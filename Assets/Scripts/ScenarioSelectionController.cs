using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Everything specific to the Scenario Selection panel: populating the dropdown from
// GET /api/scenarios, showing the selected scenario's scn_description in a read-only field,
// having the Virtual Assistant bot read that description aloud whenever the selection
// changes, the "Use server's AI token" checkbox, and Cancel/Start. Deliberately knows nothing
// about the Welcome Screen panel or the world-interaction lock - it only ever raises
// OnCancelRequested/OnStartRequested and lets WelcomeScreenController decide what those mean
// (mirrors ActionMenuUI being the pure view while ActionMenuController owns orchestration).
//
// ASSUMES the dropdown/description field are TextMeshPro's TMP_Dropdown/TMP_InputField, since
// every other piece of UI already built in this project (SubtitleController, the action menu)
// uses TextMeshPro rather than Unity's legacy UI Text/InputField/Dropdown. If your placeholder
// objects actually use the legacy `UnityEngine.UI.Dropdown`/`InputField` instead, swap the two
// `using TMPro`/field types below accordingly - the rest of this class's logic is unaffected
// either way.
public class ScenarioSelectionController : MonoBehaviour
{
    [Header("Backend")]
    [SerializeField] private BackendConfig backendConfig;
    [SerializeField] private float timeoutSeconds = 10f;

    [Header("UI")]
    [Tooltip("Populated with one entry per scenario, in the order the backend returns them.")]
    [SerializeField] private TMP_Dropdown scenarioDropdown;

    [Tooltip("Shows the selected scenario's scn_description. Locked read-only in Awake() - the " +
             "trainee only ever reads this, never types into it.")]
    [SerializeField] private TMP_InputField descriptionField;

    [Tooltip("\"Use server's AI token\" - checked means overwrite CredentialStorage with the " +
             "server's OpenAI token when Start is clicked; unchecked leaves whatever token the " +
             "trainee already has in CredentialStorage completely untouched.")]
    [SerializeField] private Toggle useServerTokenToggle;

    [SerializeField] private Button cancelButton;
    [Tooltip("Scenario Selection's OWN Start button - the one that actually launches the " +
             "simulation, distinct from the Welcome Screen's Start button that only opens this panel.")]
    [SerializeField] private Button startButton;

    [Header("Bot readback (optional)")]
    [Tooltip("If assigned, the bot speaks the selected scenario's description aloud every time " +
             "the selection changes (including the very first one, once the list finishes " +
             "loading). Leave empty to disable the readback entirely - the description field " +
             "still updates either way.")]
    [SerializeField] private VirtualAssistantPersona virtualAssistantPersona;

    // Raised when Cancel is clicked - WelcomeScreenController re-shows the Welcome Screen.
    public event Action OnCancelRequested;

    // Raised when Start is clicked, carrying (scn_id, useServerToken) - WelcomeScreenController
    // does everything that actually launching the simulation requires (token fetch, stage
    // load, SessionManager.SkipToScenarioActive()). This class never touches any of that
    // directly, so it has no idea what "starting a scenario" actually involves beyond these
    // two values.
    public event Action<int, bool> OnStartRequested;

    private ScenarioDto[] loadedScenarios = Array.Empty<ScenarioDto>();
    private Coroutine fetchRoutine;

    private void Awake()
    {
        if (descriptionField != null)
        {
            // Both flags, deliberately - interactable alone still lets a few TMP versions
            // focus/caret-blink the field on click; readOnly is the field built specifically
            // to mean "display text, never edit it," which is exactly what's wanted here.
            descriptionField.interactable = false;
            descriptionField.readOnly = true;
        }

        cancelButton?.onClick.AddListener(HandleCancelClicked);
        startButton?.onClick.AddListener(HandleStartClicked);
        scenarioDropdown?.onValueChanged.AddListener(HandleDropdownValueChanged);
    }

    private void OnDestroy()
    {
        cancelButton?.onClick.RemoveListener(HandleCancelClicked);
        startButton?.onClick.RemoveListener(HandleStartClicked);
        scenarioDropdown?.onValueChanged.RemoveListener(HandleDropdownValueChanged);
    }

    // Called by WelcomeScreenController right after it activates this panel. Fetches fresh
    // every time rather than caching the list for the lifetime of the app, so a tutor's
    // mid-session edit in the portal (a new scenario published, a tweaked description) shows
    // up the next time the trainee opens this screen without needing the build restarted -
    // the same "tutor edits it, Unity just reflects it" principle behind the whole backend
    // (project doc section 16 onward). Guarded with a stored Coroutine reference so a rapid
    // Cancel -> Start -> Cancel -> Start doesn't stack up multiple in-flight fetches racing
    // each other into the dropdown.
    public void OnPanelShown()
    {
        if (fetchRoutine != null) StopCoroutine(fetchRoutine);
        fetchRoutine = StartCoroutine(FetchAndPopulate());
    }

    private IEnumerator FetchAndPopulate()
    {
        if (scenarioDropdown == null)
        {
            Debug.LogError("[ScenarioSelectionController] No Scenario Dropdown assigned - cannot populate anything.");
            yield break;
        }

        scenarioDropdown.interactable = false;
        scenarioDropdown.ClearOptions();
        if (descriptionField != null) descriptionField.text = "Loading scenarios...";
        if (startButton != null) startButton.interactable = false;

        ScenarioDto[] scenarios = null;
        string error = null;
        yield return RemoteScenarioApiLoader.FetchScenarioList(backendConfig, timeoutSeconds,
            onSuccess: list => scenarios = list,
            onError: err => error = err);

        fetchRoutine = null;

        if (scenarios == null || scenarios.Length == 0)
        {
            Debug.LogError($"[ScenarioSelectionController] Could not populate the scenario list " +
                             $"({error ?? "backend returned zero scenarios"}).");
            if (descriptionField != null)
                descriptionField.text = "Could not load scenarios from the server - check the Console and the backend connection.";
            yield break;
        }

        loadedScenarios = scenarios;

        scenarioDropdown.AddOptions(
            scenarios.Select(s => string.IsNullOrWhiteSpace(s.scn_name) ? $"Scenario {s.scn_id}" : s.scn_name).ToList());
        scenarioDropdown.interactable = true;
        if (startButton != null) startButton.interactable = true;

        // SetValueWithoutNotify + a manual ApplySelection call, rather than just SetValue(0) or
        // letting AddOptions' own default selection fire onValueChanged - AddOptions selecting
        // index 0 by default WOULD invoke onValueChanged on its own in most TMP_Dropdown
        // versions, but relying on that is fragile/version-dependent; doing it explicitly here
        // guarantees exactly one ApplySelection call (and exactly one TTS readback) for the
        // initial population, not zero and not two.
        scenarioDropdown.SetValueWithoutNotify(0);
        ApplySelection(0);
    }

    private void HandleDropdownValueChanged(int index) => ApplySelection(index);

    private void ApplySelection(int index)
    {
        if (loadedScenarios.Length == 0 || index < 0 || index >= loadedScenarios.Length) return;

        ScenarioDto dto = loadedScenarios[index];
        string description = dto.scn_description ?? "";

        if (descriptionField != null) descriptionField.text = description;

        if (virtualAssistantPersona != null && !string.IsNullOrWhiteSpace(description))
            virtualAssistantPersona.SpeakAnnouncement(description);
    }

    private void HandleCancelClicked()
    {
        if (fetchRoutine != null)
        {
            StopCoroutine(fetchRoutine);
            fetchRoutine = null;
        }
        OnCancelRequested?.Invoke();
    }

    private void HandleStartClicked()
    {
        if (loadedScenarios.Length == 0 || scenarioDropdown == null)
        {
            Debug.LogWarning("[ScenarioSelectionController] Start clicked with no scenario loaded/selected - ignoring.");
            return;
        }

        int index = Mathf.Clamp(scenarioDropdown.value, 0, loadedScenarios.Length - 1);
        ScenarioDto selected = loadedScenarios[index];
        bool useServerToken = useServerTokenToggle != null && useServerTokenToggle.isOn;

        OnStartRequested?.Invoke(selected.scn_id, useServerToken);
    }
}
