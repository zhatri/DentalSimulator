using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

// Fetches GET /api/tools-actions (the backend's TOOL/TOOL_ACTION join) into a
// ToolsActionsCatalog. Purely a validation/reference aid right now - see that class's
// comment. Non-blocking: does not gate scenario start the way RemoteScenarioApiLoader
// does, since nothing in the core advance flow actually depends on this data existing.
public class RemoteToolsActionsLoader : MonoBehaviour
{
    [SerializeField] private BackendConfig backendConfig;
    [SerializeField] private ToolsActionsCatalog catalog;
    [SerializeField] private float timeoutSeconds = 10f;

    private void Start()
    {
        StartCoroutine(LoadAndApply());
    }

    private IEnumerator LoadAndApply()
    {
        if (catalog == null)
        {
            Debug.LogError("[RemoteToolsActionsLoader] No ToolsActionsCatalog assigned - nothing to apply the loaded entries to.");
            yield break;
        }

        if (backendConfig == null || string.IsNullOrWhiteSpace(backendConfig.baseUrl))
        {
            Debug.LogWarning("[RemoteToolsActionsLoader] No BackendConfig/baseUrl assigned - skipping.");
            yield break;
        }

        string url = backendConfig.baseUrl.TrimEnd('/') + "/api/tools-actions";
        using UnityWebRequest request = UnityWebRequest.Get(url);
        request.timeout = Mathf.CeilToInt(timeoutSeconds);

        // See the identical guard/comment in RemoteScenarioApiLoader.LoadAndApply() -
        // SendWebRequest() can throw synchronously, and a try/catch can't wrap a yield
        // return directly.
        UnityWebRequestAsyncOperation operation;
        try
        {
            operation = request.SendWebRequest();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RemoteToolsActionsLoader] SendWebRequest() threw for \"{url}\" ({ex.GetType().Name}: {ex.Message}) - " +
                             "if this is InvalidOperationException/\"Insecure connection not allowed\", see Player Settings > Other " +
                             "Settings > Configuration > \"Allow downloads over HTTP*\" for this build target.");
            yield break;
        }

        yield return operation;

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[RemoteToolsActionsLoader] Failed to fetch tools/actions ({request.error}, HTTP {request.responseCode}).");
            yield break;
        }

        try
        {
            ToolActionDto[] dtos = JsonArrayUtil.FromJsonArray<ToolActionDto>(request.downloadHandler.text);
            List<ToolsActionsCatalog.Entry> entries = dtos.Select(d => new ToolsActionsCatalog.Entry
            {
                toolId = d.tol_id,
                toolName = d.tol_name,
                actionId = d.act_id
            }).ToList();

            catalog.SetEntries(entries);
            Debug.Log($"[RemoteToolsActionsLoader] Loaded {entries.Count} tool/action entr{(entries.Count == 1 ? "y" : "ies")}.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RemoteToolsActionsLoader] Failed to parse tools/actions response ({ex.Message}).");
        }
    }
}
