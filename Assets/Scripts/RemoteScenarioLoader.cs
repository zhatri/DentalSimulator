using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// Lets a tutor/educator author and edit the scenario's stage sequence in a spreadsheet
// instead of Unity ScriptableObject assets - no Unity Editor access needed at all. Point
// this at a published CSV URL (see setup notes below) and it fetches + parses that sheet
// at runtime on the headset or desktop simulator, building the exact same
// PatientStageDefinition/PatientStageSequence objects the rest of the app already
// understands. SofiaPersona and PatientScenarioController don't know or care whether a
// stage came from a spreadsheet or a hand-authored asset in the Project window - same
// public fields either way, which is why this needed no changes to either of those files.
//
// RECOMMENDED HOSTING (free, no backend): Google Sheets - File > Share > Publish to web >
// choose the sheet > "Comma-separated values (.csv)" > Publish. Copy the resulting URL into
// csvUrl below. Whenever the tutor edits and saves the sheet, that same URL serves the
// latest content within a few minutes - no republish step, no app rebuild, no Unity needed.
//
// CSV COLUMN SCHEMA - first row must be this exact header (any column order is fine, the
// parser looks columns up by name, not position):
//   stageId, stageOrder, scenarioStage, currentVitals, stageGuardrail, narrationForTrainee,
//   conversationCanAdvanceStage, advanceCondition, advanceTriggerPhrases, fixedAdvanceResponse
//
// Notes for the tutor filling this in:
// - stageOrder: a plain integer (0, 1, 2, ...). Rows are sorted by this value, not by their
//   position in the sheet, so reordering rows is a nice-to-have for readability, not required.
// - conversationCanAdvanceStage: TRUE or FALSE (case-insensitive).
// - advanceTriggerPhrases: multiple phrases in ONE cell, separated by a pipe character "|",
//   e.g.   are you ready|ready for the procedure|shall we begin
// - A cell containing a comma, or even a line break, is fine as ordinary text - Google
//   Sheets automatically quotes it when publishing the CSV, and the parser below handles
//   quoted fields (including a doubled "" as an escaped quote inside one).
// - Leave fixedAdvanceResponse blank to let the LLM generate Sofia's reply as usual for
//   that stage; fill it in (e.g. "Yes, I'm ready.") to have her speak that exact line
//   instead whenever the advance phrase matches.
//
// RESILIENCE: if csvUrl is empty, or the fetch/parse fails for any reason (no network on
// the headset, the sheet was unpublished, a malformed row), this falls back to whatever
// PatientStageSequence asset is already assigned in the Inspector on PatientScenarioController,
// rather than leaving the trainee stuck on a loading screen because of a wifi hiccup.
public class RemoteScenarioLoader : MonoBehaviour
{
    [Header("Where the tutor-authored scenario lives")]
    [Tooltip("The published CSV URL from Google Sheets (File > Share > Publish to web > CSV).")]
    [SerializeField] private string csvUrl;

    [Tooltip("How long to wait for the sheet before giving up and falling back to the local " +
             "PatientStageSequence asset already assigned on PatientScenarioController.")]
    [SerializeField] private float timeoutSeconds = 10f;

    [Header("Wiring")]
    [SerializeField] private PatientScenarioController patientScenarioController;

    private void Awake()
    {
        // Must happen in Awake, not Start - Unity runs Awake on every active object in the
        // scene before Start runs on any of them, so this is guaranteed to land before
        // SessionManager.Start() gets a chance to call PatientScenarioController.BeginScenario().
        patientScenarioController?.WaitForRemoteContent();
    }

    private void Start()
    {
        StartCoroutine(LoadAndApply());
    }

    private IEnumerator LoadAndApply()
    {
        if (patientScenarioController == null)
        {
            Debug.LogError("[RemoteScenarioLoader] No PatientScenarioController assigned - nothing to apply the loaded stages to.");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(csvUrl))
        {
            Debug.LogWarning("[RemoteScenarioLoader] No csvUrl set - skipping remote load, using the local PatientStageSequence asset instead.");
            patientScenarioController.UseFallbackStageSequence();
            yield break;
        }

        using UnityWebRequest request = UnityWebRequest.Get(csvUrl);
        request.timeout = Mathf.CeilToInt(timeoutSeconds);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[RemoteScenarioLoader] Failed to fetch scenario CSV ({request.error}) - falling back to the local PatientStageSequence asset.");
            patientScenarioController.UseFallbackStageSequence();
            yield break;
        }

        PatientStageDefinition[] stages;
        try
        {
            stages = ParseStages(request.downloadHandler.text);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RemoteScenarioLoader] Failed to parse scenario CSV ({ex.Message}) - falling back to the local PatientStageSequence asset.");
            patientScenarioController.UseFallbackStageSequence();
            yield break;
        }

        if (stages.Length == 0)
        {
            Debug.LogError("[RemoteScenarioLoader] Parsed CSV produced zero stages - falling back to the local PatientStageSequence asset.");
            patientScenarioController.UseFallbackStageSequence();
            yield break;
        }

        var sequence = ScriptableObject.CreateInstance<PatientStageSequence>();
        sequence.stages = stages;
        Debug.Log($"[RemoteScenarioLoader] Loaded {stages.Length} stage(s) from the remote spreadsheet.");
        patientScenarioController.SetStageSequence(sequence);
    }

    private static PatientStageDefinition[] ParseStages(string csvText)
    {
        List<string[]> rows = ParseCsv(csvText);
        if (rows.Count < 2) throw new Exception("CSV has no data rows (only a header, or nothing at all).");

        string[] header = rows[0];
        int Col(string name)
        {
            int index = Array.FindIndex(header, h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new Exception($"Missing expected column \"{name}\" in the CSV header.");
            return index;
        }

        int cStageId = Col("stageId");
        int cStageOrder = Col("stageOrder");
        int cScenarioStage = Col("scenarioStage");
        int cCurrentVitals = Col("currentVitals");
        int cStageGuardrail = Col("stageGuardrail");
        int cNarrationForTrainee = Col("narrationForTrainee");
        int cConversationCanAdvanceStage = Col("conversationCanAdvanceStage");
        int cAdvanceCondition = Col("advanceCondition");
        int cAdvanceTriggerPhrases = Col("advanceTriggerPhrases");
        int cFixedAdvanceResponse = Col("fixedAdvanceResponse");

        var stages = new List<PatientStageDefinition>();
        for (int i = 1; i < rows.Count; i++)
        {
            string[] row = rows[i];
            if (row.Length == 1 && string.IsNullOrWhiteSpace(row[0])) continue; // skip blank trailing rows

            var stage = ScriptableObject.CreateInstance<PatientStageDefinition>();
            stage.stageId = Get(row, cStageId);
            stage.stageOrder = int.TryParse(Get(row, cStageOrder), NumberStyles.Integer, CultureInfo.InvariantCulture, out int order) ? order : i;
            stage.scenarioStage = Get(row, cScenarioStage);
            stage.currentVitals = Get(row, cCurrentVitals);
            stage.stageGuardrail = Get(row, cStageGuardrail);
            stage.narrationForTrainee = Get(row, cNarrationForTrainee);
            stage.conversationCanAdvanceStage = string.Equals(Get(row, cConversationCanAdvanceStage).Trim(), "TRUE", StringComparison.OrdinalIgnoreCase);
            stage.advanceCondition = Get(row, cAdvanceCondition);
            // TrimEntries isn't available on the .NET Standard 2.0 profile Unity's IL2CPP
            // build commonly targets - split, then trim each entry manually instead.
            stage.advanceTriggerPhrases = Get(row, cAdvanceTriggerPhrases)
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToArray();
            stage.fixedAdvanceResponse = Get(row, cFixedAdvanceResponse);

            stages.Add(stage);
        }

        stages.Sort((a, b) => a.stageOrder.CompareTo(b.stageOrder));
        return stages.ToArray();
    }

    private static string Get(string[] row, int index) => index < row.Length ? row[index] : "";

    // Minimal RFC4180-style CSV parser: handles quoted fields, embedded commas, embedded
    // newlines inside a quoted cell, and "" as an escaped quote - all things Google Sheets
    // produces automatically when a tutor's cell text happens to contain a comma or a line
    // break (e.g. a longer guardrail or narration sentence).
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var field = new StringBuilder();
        var row = new List<string>();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break; // ignore - the \n that follows (CRLF) ends the row below
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row.ToArray());
                    row = new List<string>();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        // Final field/row if the text doesn't end with a trailing newline.
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }
}