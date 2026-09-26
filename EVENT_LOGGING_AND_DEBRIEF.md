# Virtual Assistant — Event Logging & Debrief

## Implemented

The scenario controller automatically creates a persistent `SessionTelemetry` service when a valid scenario starts. No new GameObject or scene wiring is required for logging or the desktop debrief.

- Each run receives a unique session ID, scenario ID, UTC timestamps and ordered events.
- Events cover stage entry/completion, accepted trainee dialogue addressed to the patient or assistant, patient speech chunks, assistant confirmations, requested/disclosed hints, correct/incorrect action IDs, conversation-driven advancement, debug advances and session completion/abort.
- Hints count when their text is dispatched to speech, not simply when requested. Speech dispatch does not prove that the audio finished playing or was heard. Patient streaming chunks are recorded once per dispatched chunk, rather than repeatedly logging the full accumulating response.
- Replies retain the stage and session captured when the turn started. Finalized logs reject further events.
- A deterministic debrief reports progress, hints and incorrect actions, with stage-specific hint/mistake details. Outcome is `completed` or `aborted`, not a clinical assessment. Debug advances are explicitly flagged for review.
- The desktop debrief displays automatically after the final stage; mouse movement is suspended while it is visible. It has a Retry upload button.
- A local JSON audit trail is checkpointed after each event. Completed results are queued before any upload. Failed requests remain queued. The queue is retried on backend configuration and through Retry upload. Successful uploads retain a local archive.

## Run and connect

1. Open this project in its declared Unity **6000.5.10f1**, restore project packages/assets and use the existing Desktop scene/setup from README.md.
2. In the existing shared `BackendConfig` asset, set `baseUrl` to the team's backend.
3. Agree the POST contract below with the portal teammate, then set the new `sessionResultsPath` to the implemented route (for example `/api/session-results`). The default is blank because the supplied archive contains no results endpoint or portal source. A blank endpoint still permits logging and debriefs and clearly reports that upload is not configured.
4. `RemoteScenarioApiLoader` passes the selected scenario ID and shared backend configuration to telemetry automatically. For Inspector-authored/local scenarios without this loader, the sequence asset name identifies the scenario; call `SessionTelemetry.Ensure().Configure(backendConfig)` from your bootstrap if uploads are wanted.
5. Play a scenario, ask for a hint, speak to the patient, perform an incorrect action, then complete the stages. Inspect the debrief and saved JSON.

Files are under `Application.persistentDataPath/SessionResults`:

- `<sessionId>.active.json`: latest in-progress checkpoint.
- `<sessionId>.pending.json`: finalized result awaiting delivery.
- `<sessionId>.uploaded.json`: server accepted the result with HTTP 2xx.

Normal quit/scene destruction records an abort. A hard crash can leave an `.active.json` checkpoint; it is retained for recovery, but is not automatically treated as a completed or aborted submission. Results contain dialogue; apply the team's access and retention rules to these files.

## Portal contract — proposed, requires backend implementation

The client makes a JSON POST to `baseUrl + sessionResultsPath`, with `Content-Type: application/json` and `Idempotency-Key: <sessionId>`. The payload is the `SessionRecord` object:

```json
{
  "schemaVersion": 1,
  "sessionId": "a unique 32-character GUID",
  "scenarioId": "1",
  "startedAtUtc": "2026-09-22T11:00:00.0000000Z",
  "endedAtUtc": "2026-09-22T11:05:00.0000000Z",
  "outcome": "completed",
  "summary": "Scenario completed. ...",
  "totalStages": 2,
  "completedStages": 2,
  "hintsDisclosed": 1,
  "incorrectActions": 1,
  "events": [
    {
      "sequence": 1,
      "timestampUtc": "2026-09-22T11:00:00.0000000Z",
      "stageId": "",
      "type": "session_started",
      "actor": "system",
      "text": ""
    }
  ]
}
```

The portal must validate the payload, persist the session ID/outcome (and any audit detail it supports), and return HTTP 2xx only after durable acceptance. Use a unique session ID constraint/upsert: if a response is lost, retrying the same session must not create a duplicate. The portal should return 2xx for an already accepted identical session; conflicting data should be rejected. The client does not interpret a response body or treat HTTP 409 as success. Its timeout is 15 seconds.

No portal credentials, login session or authenticated POST contract were supplied. The current client sends no authorization header. If the actual portal requires authentication or uses different field names, integrate its established auth/payload contract in `UploadPending` before deploying. No live upload was performed or verified.

## UI extension

`SessionTelemetry.Current`, `UploadStatus`, `OnDebriefReady` and `RetryUploads()` are available for the group's UI. The included immediate-mode panel is a desktop fallback. For Quest, bind a world-space Canvas to these values; the desktop panel is not an XR UI. Read `Current` on enable as well as subscribing to the event, so a panel enabled after completion still shows the debrief.

## Validation

Executed: standalone C# checks for unique session IDs, ordered UTC events, dialogue storage, hint-request versus disclosure counts, incorrect actions, deterministic summaries, completion, repeated finalization, rejection of late events and aborts. New `SessionRecord`, `SessionTelemetry` and updated `BackendConfig` compiled against the installed Unity 6000.3.23f1 assemblies.

Run the portable model checks with .NET 10:

```text
dotnet run --project Tests/SessionRecordChecks/SessionRecordChecks.csproj
```

The full Unity scene, Meta speech integrations, disk/network behavior in a Player, and portal acceptance require integration testing. The installed editor is 6000.3.23f1; the project declares 6000.5.10f1, so a full project import/play test was not performed.

Manual acceptance checks:

1. Trigger a wrong action on two different stages; verify both IDs and stages appear in the log/debrief.
2. Request hints with the LLM enabled and with the fixed-hint fallback. A failed or empty response must not increment disclosed hints.
3. Speak with Sofia with streaming enabled/disabled and use a fixed confirmation. Verify trainee lines and dispatched patient chunks without duplicated full responses.
4. Finish the last stage and attempt another action/advance. Verify exactly one final result and no extra completed stages.
5. Use the debug key. Verify the debrief flags it, without claiming a clinical pass.
6. Configure the portal, submit, verify its saved ID/outcome and the local `.uploaded.json`. Disconnect the backend and repeat: the result must stay `.pending.json`; restart/retry after reconnecting and verify one portal record.
7. Quit mid-scenario, then launch again with the backend configured. Verify the aborted pending result is submitted. Simulate a hard crash separately and verify the active checkpoint remains available.

## Changed files

New: `Assets/Scripts/SessionRecord.cs`, `SessionTelemetry.cs`, their Unity metadata, this guide and portable model checks.

Updated: `BackendConfig.cs`, `RemoteScenarioApiLoader.cs`, `PatientScenarioController.cs`, `SofiaPersona.cs`, `VirtualAssistantPersona.cs`, `PlayerController.cs`, `ActionMenuController.cs`.

This contribution preserves existing assets and scene wiring. No new AI service or API key is needed for logging/debriefs.
