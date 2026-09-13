using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Meta.XR.BuildingBlocks.AIBlocks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

// Fetches the OpenAI API key from the team's own backend (Database.xlsx's OPENAI sheet,
// served via GET /api/openai-token) and, if it differs from what's currently stored,
// overwrites it directly on the SAME CredentialStorage asset MBB's Agents already read
// from - confirmed against the actual Credential_Storage.asset file (a plain YAML
// ScriptableObject: `entries: [{ providerId, apiKey }, ...]`), which settles what the
// earlier version of this class could only speculate about (whether there was any way to
// push a fetched token into the running MBB pipeline at all).
//
// Runs at Awake (via [DefaultExecutionOrder(-1000)], so it fires before most other
// scripts') rather than Start, since this needs to land before any Agent actually sends a
// request using this key - if that turns out not to be early enough (e.g. an Agent reads/
// caches the key even earlier, in its own Awake), move whatever GameObject this lives on
// so it's guaranteed to run first (Project Settings > Script Execution Order, or a
// dedicated bootstrap scene loaded before the scenario scene).
//
// USES REFLECTION rather than a documented public API on CredentialStorage, because only
// the asset's SERIALIZED shape was directly confirmed (its YAML), not its C# source. This
// is why it targets the exact field names visible in that YAML (`entries`, `providerId`,
// `apiKey`) rather than a compile-time-checked property - if a future MBB update renames
// those fields, this will start logging the "could not find" error below rather than
// throwing at compile time, so it fails loud, not silent. If Meta's CredentialStorage
// class turns out to expose a public setter (check its source under Packages/ in your
// project), switch to calling that directly instead - it would be strictly more robust
// than this reflection-based approach.
[DefaultExecutionOrder(-1000)]
[DisallowMultipleComponent]
public class RemoteOpenAiTokenLoader : MonoBehaviour
{
    [SerializeField] private BackendConfig backendConfig;

    [Tooltip("Drag the SAME Credential_Storage asset your MBB Agents/providers already " +
             "reference here - not a copy of it. Leave empty to only fetch/expose the token " +
             "(via Token/OnTokenReceived) without touching CredentialStorage at all.")]
    [SerializeField] private CredentialStorage credentialStorage;

    [SerializeField] private string providerId = "OpenAI";

    [Tooltip("Fires once the token is fetched (before/independent of the CredentialStorage " +
             "update attempt above) - wire this up for logging/debug display if useful.")]
    [SerializeField] private StringUnityEvent onTokenReceived;

    public string Token { get; private set; }
    public event Action<string> OnTokenReceived;

    private void Awake()
    {
        StartCoroutine(Fetch());
    }

    private IEnumerator Fetch()
    {
        if (backendConfig == null || string.IsNullOrWhiteSpace(backendConfig.baseUrl))
        {
            Debug.LogError("[RemoteOpenAiTokenLoader] No BackendConfig/baseUrl assigned - cannot fetch the OpenAI token.");
            yield break;
        }

        string url = backendConfig.baseUrl.TrimEnd('/') + "/api/openai-token";
        using UnityWebRequest request = UnityWebRequest.Get(url);

        // See the identical guard/comment in RemoteScenarioApiLoader.LoadAndApply() -
        // SendWebRequest() can throw synchronously (e.g. "Insecure connection not allowed"
        // for a plain http:// URL in a built Player with Player Settings > Allow downloads
        // over HTTP* set to Not Allowed), and a try/catch can't wrap a yield return directly.
        UnityWebRequestAsyncOperation operation;
        try
        {
            operation = request.SendWebRequest();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RemoteOpenAiTokenLoader] SendWebRequest() threw for \"{url}\" ({ex.GetType().Name}: {ex.Message}) - " +
                             "if this is InvalidOperationException/\"Insecure connection not allowed\", see Player Settings > Other " +
                             "Settings > Configuration > \"Allow downloads over HTTP*\" for this build target. Keeping whatever key " +
                             "is already in CredentialStorage.");
            yield break;
        }

        yield return operation;

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[RemoteOpenAiTokenLoader] Failed to fetch OpenAI token ({request.error}, HTTP {request.responseCode}) - " +
                             "keeping whatever key is already in CredentialStorage.");
            yield break;
        }

        OpenAiTokenDto dto;
        try
        {
            dto = JsonUtility.FromJson<OpenAiTokenDto>(request.downloadHandler.text);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RemoteOpenAiTokenLoader] Failed to parse OpenAI token response ({ex.Message}).");
            yield break;
        }

        if (dto == null || string.IsNullOrWhiteSpace(dto.oai_token))
        {
            Debug.LogError("[RemoteOpenAiTokenLoader] Token response parsed but oai_token was empty.");
            yield break;
        }

        Token = dto.oai_token;
        Debug.Log("[RemoteOpenAiTokenLoader] OpenAI token fetched from backend.");
        OnTokenReceived?.Invoke(Token);
        onTokenReceived?.Invoke(Token);

        if (credentialStorage != null) ApplyToCredentialStorage(Token);
    }

    private void ApplyToCredentialStorage(string newToken)
    {
        object entry = FindEntry(credentialStorage, providerId, out FieldInfo apiKeyField);
        if (entry == null || apiKeyField == null)
        {
            Debug.LogError($"[RemoteOpenAiTokenLoader] Could not find a \"{providerId}\" entry inside " +
                             $"\"{credentialStorage.name}\" via reflection - CredentialStorage's internal " +
                             "field names may not match entries/providerId/apiKey any more. Not applied.");
            return;
        }

        string currentValue = apiKeyField.GetValue(entry) as string;
        if (string.Equals(currentValue, newToken, StringComparison.Ordinal))
        {
            Debug.Log("[RemoteOpenAiTokenLoader] Fetched token matches what's already in CredentialStorage - no change made.");
            return;
        }

        apiKeyField.SetValue(entry, newToken);
        Debug.Log($"[RemoteOpenAiTokenLoader] Updated the \"{providerId}\" entry's apiKey on \"{credentialStorage.name}\" " +
                    "in memory (the running instance only - this does NOT write back to the .asset file on disk, " +
                    "and only persists for this play/app session, which is exactly what's needed here).");
    }

    // Walks CredentialStorage's own "entries" field (a list of some Entry-like type with
    // its own "providerId"/"apiKey" fields) via reflection, since the concrete Entry type
    // isn't part of any documented public API this script can reference directly - only
    // the serialized field NAMES are known, confirmed from the actual .asset file's YAML.
    private static object FindEntry(object storage, string providerId, out FieldInfo apiKeyField)
    {
        apiKeyField = null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        Type storageType = storage.GetType();
        FieldInfo entriesField = storageType.GetField("entries", flags);
        if (entriesField == null)
        {
            Debug.LogError($"[RemoteOpenAiTokenLoader] {storageType.Name} has no \"entries\" field.");
            return null;
        }

        if (entriesField.GetValue(storage) is not IEnumerable entries)
        {
            Debug.LogError("[RemoteOpenAiTokenLoader] \"entries\" wasn't enumerable - unexpected type.");
            return null;
        }

        foreach (object entry in entries)
        {
            if (entry == null) continue;

            Type entryType = entry.GetType();
            FieldInfo providerIdField = entryType.GetField("providerId", flags);
            if (providerIdField == null) continue;

            if (string.Equals(providerIdField.GetValue(entry) as string, providerId, StringComparison.OrdinalIgnoreCase))
            {
                apiKeyField = entryType.GetField("apiKey", flags);
                return entry;
            }
        }

        return null;
    }
}

// Concrete UnityEvent<string> subclass - UnityEvent<T> itself can't be serialized/shown
// in the Inspector directly, only a named subclass of it can.
[Serializable]
public class StringUnityEvent : UnityEvent<string> { }
