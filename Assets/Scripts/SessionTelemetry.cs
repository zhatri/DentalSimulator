using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// Created automatically by the scenario controller; survives scene changes so uploads can finish.
public class SessionTelemetry : MonoBehaviour
{
    public static SessionTelemetry Instance { get; private set; }
    public SessionRecord Current { get; private set; }
    public string CurrentStageId => stageId;
    public bool IsDebriefVisible => showDebrief;
    public string UploadStatus { get; private set; } = "Not submitted";
    public event Action<SessionRecord> OnDebriefReady;
    private BackendConfig config;
    private string stageId = "";
    private bool uploading, showDebrief, quitting;
    private Vector2 scroll;
    private string DirectoryPath => Path.Combine(Application.persistentDataPath, "SessionResults");

    public static SessionTelemetry Ensure()
    {
        if (Instance == null)
        {
            var owner = new GameObject("Session Telemetry");
            owner.AddComponent<SessionTelemetry>();
        }
        return Instance;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void Configure(BackendConfig backend) { config = backend; RetryUploads(); }

    public void Begin(string scenarioId, int stages)
    {
        if (Current != null && Current.outcome == "in_progress") Finish(false);
        Current = new SessionRecord(scenarioId, stages);
        stageId = "";
        showDebrief = false;
        UploadStatus = "Not submitted";
        Log("session_started", "system");
    }

    public void EnterStage(string id)
    {
        stageId = id;
        Log("stage_started", "system");
    }

    public void Log(string type, string actor = "", string text = "")
    {
        if (Current == null || Current.outcome != "in_progress") return;
        Current.Add(stageId, type, actor, text);
        Save(".active.json");
    }

    public void LogDialogue(string session, string stage, string type, string actor, string text)
    {
        if (Current == null || Current.sessionId != session || Current.outcome != "in_progress") return;
        Current.Add(stage, type, actor, text);
        Save(".active.json");
    }

    public void Finish(bool completed)
    {
        if (Current == null || Current.outcome != "in_progress") return;
        Current.Finish(completed);
        showDebrief = true;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (Save(".pending.json"))
        {
            try { File.Delete(Path.Combine(DirectoryPath, Current.sessionId + ".active.json")); }
            catch (IOException) { /* The pending copy remains available. */ }
        }
        if (!quitting)
        {
            OnDebriefReady?.Invoke(Current);
            RetryUploads();
        }
    }

    private bool Save(string suffix)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            string path = Path.Combine(DirectoryPath, Current.sessionId + suffix);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(Current, true));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
            return true;
        }
        catch (Exception ex)
        {
            UploadStatus = "Local save failed: " + ex.Message;
            Debug.LogError("[SessionTelemetry] " + UploadStatus);
            return false;
        }
    }

    public void RetryUploads()
    {
        if (uploading) return;
        if (Current != null && Current.outcome != "in_progress"
            && !File.Exists(Path.Combine(DirectoryPath, Current.sessionId + ".pending.json"))
            && !File.Exists(Path.Combine(DirectoryPath, Current.sessionId + ".uploaded.json")))
            if (!Save(".pending.json")) return;
        if (config == null || string.IsNullOrWhiteSpace(config.sessionResultsPath))
        {
            UploadStatus = "Portal endpoint not configured; results retained locally.";
            return;
        }
        StartCoroutine(UploadPending());
    }

    private IEnumerator UploadPending()
    {
        uploading = true;
        try
        {
            if (!Directory.Exists(DirectoryPath)) yield break;
            foreach (string path in Directory.GetFiles(DirectoryPath, "*.pending.json"))
            {
                string json = null;
                try { json = File.ReadAllText(path); }
                catch (Exception ex) { UploadStatus = "Cannot read saved result: " + ex.Message; }
                if (json == null) continue;
                string id = Path.GetFileName(path).Replace(".pending.json", "");
                string url = config.baseUrl.TrimEnd('/') + "/" + config.sessionResultsPath.TrimStart('/');
                using (var request = new UnityWebRequest(url, "POST"))
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.timeout = 15;
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.SetRequestHeader("Idempotency-Key", id);
                    UnityWebRequestAsyncOperation operation = null;
                    try { operation = request.SendWebRequest(); }
                    catch (Exception ex) { UploadStatus = "Upload could not start: " + ex.Message; }
                    if (operation == null) continue;
                    UploadStatus = "Uploading saved results…";
                    yield return operation;
                    if (request.result == UnityWebRequest.Result.Success && request.responseCode >= 200 && request.responseCode < 300)
                    {
                        try
                        {
                            // Keep the full audit trail after delivery; never overwrite another session.
                            File.Move(path, path.Replace(".pending.json", ".uploaded.json"));
                            UploadStatus = "Results uploaded.";
                        }
                        catch (Exception ex) { UploadStatus = "Portal accepted results; local receipt failed: " + ex.Message; }
                    }
                    else UploadStatus = $"Upload failed (HTTP {request.responseCode}); saved locally. Use Retry upload.";
                }
            }
        }
        finally { uploading = false; }
    }

    private void OnApplicationQuit() { quitting = true; Finish(false); }
    private void OnDestroy() { if (Instance == this) Instance = null; }

    private void LateUpdate()
    {
        // An action menu may close after the last action completed the scenario.
        // Keep its cursor restoration from locking the newly opened debrief.
        if (!showDebrief) return;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    // Desktop fallback requires no scene wiring. For a world-space XR canvas, bind
    // OnDebriefReady/Current.summary and RetryUploads to the team's existing UI.
    private void OnGUI()
    {
        if (!showDebrief || Current == null) return;
        float width = Mathf.Min(720, Screen.width - 30);
        GUILayout.BeginArea(new Rect((Screen.width - width) / 2, 15, width, Screen.height - 30), GUI.skin.box);
        GUILayout.Label("Session debrief — " + Current.sessionId);
        scroll = GUILayout.BeginScrollView(scroll);
        GUILayout.Label(Current.summary);
        GUILayout.EndScrollView();
        GUILayout.Label(UploadStatus);
        if (GUILayout.Button("Retry upload")) RetryUploads();
        if (GUILayout.Button("Close")) showDebrief = false;
        GUILayout.EndArea();
    }
}
