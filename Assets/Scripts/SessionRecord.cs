using System;
using System.Collections.Generic;
using System.Text;

[Serializable]
public class SessionEvent
{
    public int sequence;
    public string timestampUtc, stageId, type, actor, text;
}

// Plain data and deterministic debrief logic: no network or LLM dependency.
[Serializable]
public class SessionRecord
{
    public int schemaVersion = 1;
    public string sessionId, scenarioId, startedAtUtc, endedAtUtc, outcome, summary;
    public int totalStages, completedStages, hintsDisclosed, incorrectActions;
    public List<SessionEvent> events = new List<SessionEvent>();

    public SessionRecord(string scenario, int stages)
    {
        sessionId = Guid.NewGuid().ToString("N");
        scenarioId = scenario;
        totalStages = stages;
        startedAtUtc = DateTime.UtcNow.ToString("o");
        outcome = "in_progress";
    }

    public void Add(string stage, string type, string actor = "", string text = "")
    {
        if (outcome != "in_progress") return;
        events.Add(new SessionEvent { sequence = events.Count + 1,
            timestampUtc = DateTime.UtcNow.ToString("o"), stageId = stage ?? "",
            type = type, actor = actor, text = text ?? "" });
        if (type == "hint_disclosed") hintsDisclosed++;
        if (type == "incorrect_action") incorrectActions++;
        if (type == "stage_completed") completedStages++;
    }

    public void Finish(bool completed)
    {
        if (outcome != "in_progress") return;
        Add("", "session_ended", "system", completed ? "completed" : "aborted");
        endedAtUtc = DateTime.UtcNow.ToString("o");
        outcome = completed ? "completed" : "aborted";
        var result = new StringBuilder();
        result.AppendLine(completed ? "Scenario completed." : "Scenario ended before completion.");
        result.AppendLine($"Stages completed: {completedStages}/{totalStages}. Hints disclosed: {hintsDisclosed}. Incorrect actions: {incorrectActions}.");
        result.AppendLine("Completion describes progress, not a clinical pass/fail grade.");
        foreach (var e in events)
            if (e.type == "incorrect_action" || e.type == "hint_disclosed" || e.type == "debug_advance")
                result.AppendLine($"Stage {e.stageId} — {e.type.Replace('_', ' ')}: {e.text}");
        summary = result.ToString();
    }
}
