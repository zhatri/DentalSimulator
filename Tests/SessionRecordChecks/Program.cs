using System;
class Program
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Main()
    {
        var s = new SessionRecord("42", 2);
        Check(s.sessionId != new SessionRecord("42", 2).sessionId, "Unique session IDs");
        s.Add("A", "stage_started");
        s.Add("A", "hint_requested", "trainee", "Help");
        Check(s.hintsDisclosed == 0, "A request alone must not count as disclosure");
        s.Add("A", "hint_disclosed", "assistant", "Check the patient");
        s.Add("A", "conversation", "trainee_to_patient", "How do you feel?");
        s.Add("A", "conversation", "patient", "Unwell");
        Check(s.events[4].stageId == "A" && s.events[4].actor == "patient" && s.events[4].text == "Unwell", "Dialogue attribution");
        s.Add("A", "incorrect_action", "trainee", "wrong_tool");
        s.Add("A", "stage_completed");
        s.Add("B", "stage_started");
        s.Add("B", "stage_completed");
        s.Finish(true);
        Check(s.outcome == "completed" && s.completedStages == 2, "Completion");
        Check(s.hintsDisclosed == 1 && s.incorrectActions == 1, "Counters");
        Check(s.summary.Contains("wrong_tool") && s.summary.Contains("Check the patient"), "Actionable debrief");
        for(int i=0;i<s.events.Count;i++) {
            Check(s.events[i].sequence == i+1, "Ordered events");
            Check(DateTime.TryParse(s.events[i].timestampUtc, out _), "Timestamps");
        }
        int count = s.events.Count;
        s.Finish(false); s.Add("B", "incorrect_action");
        Check(s.events.Count == count && s.outcome == "completed", "Idempotent finish and frozen final log");
        var aborted = new SessionRecord("42", 2);
        aborted.Finish(false);
        Check(aborted.outcome == "aborted" && aborted.completedStages == 0, "Aborted outcome");
        Console.WriteLine("PASS: session IDs, stage attribution, dialogue, disclosure counts, mistakes, summary, timestamps, finalization and abort.");
    }
}
