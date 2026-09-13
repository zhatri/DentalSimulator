using System;
using UnityEngine;

// Unity's JsonUtility can only deserialize a JSON OBJECT, never a top-level JSON ARRAY -
// several of the backend's endpoints (GET /api/scenarios, /api/tools-actions,
// /api/scenarios/{id}/stages) return a bare array. The standard workaround: wrap the raw
// array text in a throwaway object with one field, then unwrap it.
public static class JsonArrayUtil
{
    [Serializable]
    private class Wrapper<T>
    {
        public T[] items;
    }

    public static T[] FromJsonArray<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<T>();

        string wrapped = "{\"items\":" + json + "}";
        Wrapper<T> wrapper = JsonUtility.FromJson<Wrapper<T>>(wrapped);
        return wrapper?.items ?? Array.Empty<T>();
    }
}
