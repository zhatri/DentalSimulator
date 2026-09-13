using System;

// Plain JsonUtility-compatible DTOs matching the Flask backend's JSON shapes exactly
// (field names are the literal JSON keys - JsonUtility maps by exact name, case-sensitive,
// with no [JsonProperty]-style renaming available). Mirrors Database.xlsx's sheets:
// OPENAI, SCENARIO, TOOL/TOOL_ACTION (joined), and STAGE.

[Serializable]
public class OpenAiTokenDto
{
    public string oai_token;
}

[Serializable]
public class ScenarioDto
{
    public int scn_id;
    public string scn_name;
    public string scn_patient_profile;
    public string scn_guardrail;
    public string scn_description;
}

// One row of GET /api/tools-actions - the TOOL/TOOL_ACTION join.
[Serializable]
public class ToolActionDto
{
    public int tol_id;
    public string tol_name;
    public string act_id;
}

// One row of GET /api/scenarios/{scn_id}/stages - the STAGE table.
[Serializable]
public class StageDto
{
    public int scn_id;
    public int stg_id;
    public int stg_order;
    public string stg_name;
    public string stg_vital;
    public string stg_prompt;
    public string stg_guardrail;
    public string stg_bot_hint;
    public string stg_trigger;
    public string stg_trigger_phrase;
    public string stg_trigger_answer;
    public int tol_id;
    public string act_id;
    public int csc_id;
    public int pos_id;

    // NOT in Database.xlsx's STAGE sheet as given - these only get populated if the
    // backend is later changed to LEFT JOIN CUTSCENE (csc_id -> csc_file) and POSE
    // (pos_id -> pos_name) into the stages response. Entirely optional: JsonUtility just
    // leaves these null if the JSON never includes them, and RemoteScenarioApiLoader
    // falls back to the raw numeric ids (as strings) when that happens. See that class's
    // comment for the recommended backend-side fix.
    public string csc_file;
    public string pos_name;
}
