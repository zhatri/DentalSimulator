using System;
using System.Collections.Generic;
using UnityEngine;

// Runtime id -> video URL lookup for the letterboxed cutscenes referenced by
// PatientStageDefinition.cinematicId. Deliberately just a dumb dictionary holder - either
// populate entries below by hand in the Inspector for quick local testing, or let a
// RemoteCinematicCatalogLoader in the scene fetch and replace them at runtime from a
// tutor-published spreadsheet (same "content lives outside Unity" pattern as
// RemoteScenarioLoader), so adding a new AI-generated cutscene (e.g. from Dreamina) never
// needs a Unity rebuild - just a new row in the sheet.
public class CinematicCatalog : MonoBehaviour
{
    [Serializable]
    public class Entry
    {
        [Tooltip("Matches a PatientStageDefinition.cinematicId exactly (case-insensitive), " +
                 "e.g. \"sofia_syncope_collapse\".")]
        public string cinematicId;

        [Tooltip("Either (a) a direct link to the hosted video file (.mp4 recommended) - must be a " +
                 "direct-download/streamable URL, not a share-page link (a Google Drive \"view\" link " +
                 "will NOT work as-is - host on a proper file/CDN service or use Drive's direct-" +
                 "download format instead), which is the production path this was built around; or " +
                 "(b) for quick local testing before a video is actually hosted anywhere, a path to a " +
                 "video sitting under a folder literally named \"Resources\" in this project - the " +
                 "full \"Assets/Resources/Movies/sit_on_chair.mp4\" path, \"Resources/Movies/" +
                 "sit_on_chair.mp4\", or just \"Movies/sit_on_chair\" all work and resolve to the same " +
                 "place (see CinematicController.ToResourcesPath). Do NOT paste a raw \"Assets/...\" " +
                 "path expecting it to work like a URL - VideoPlayer's Url mode does not understand " +
                 "project-relative paths at all, it only recognizes http://, https://, file://, or a " +
                 "path relative to StreamingAssets; anything else here is treated as a Resources entry.")]
        public string videoUrl;
    }

    [Tooltip("Local fallback list - used until/unless a RemoteCinematicCatalogLoader replaces this " +
             "at runtime, and used as-is if no loader is present in the scene at all.")]
    [SerializeField] private List<Entry> entries = new();

    private Dictionary<string, string> lookup;

    private void Awake() => RebuildLookup();

    private void RebuildLookup()
    {
        lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (entries == null) return;

        foreach (var entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.cinematicId)) continue;
            lookup[entry.cinematicId.Trim()] = entry.videoUrl;
        }
    }

    // Called by RemoteCinematicCatalogLoader once it has fetched and parsed the tutor's sheet.
    public void SetEntries(List<Entry> newEntries)
    {
        entries = newEntries ?? new List<Entry>();
        RebuildLookup();
    }

    public bool TryGetUrl(string cinematicId, out string url)
    {
        if (lookup == null) RebuildLookup();

        if (string.IsNullOrWhiteSpace(cinematicId))
        {
            url = null;
            return false;
        }

        return lookup.TryGetValue(cinematicId.Trim(), out url) && !string.IsNullOrWhiteSpace(url);
    }
}
