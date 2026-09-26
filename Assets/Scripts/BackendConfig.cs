using UnityEngine;

// One shared place for the team's own backend's base URL, so every remote-API loader
// (scenario stages, tools/actions, the OpenAI token) points at the same server without
// duplicating the address across several Inspector fields. Currently the Flask server
// running locally at http://127.0.0.1:5000, backed by the tables in Database.xlsx -
// change baseUrl once here (e.g. once it moves off localhost onto a real host reachable
// from the headset) rather than hunting down every loader component in every scene.
[CreateAssetMenu(fileName = "BackendConfig", menuName = "Dental Simulator/Backend Config")]
public class BackendConfig : ScriptableObject
{
    [Tooltip("No trailing slash needed - loaders trim it either way. NOTE: 127.0.0.1/localhost " +
             "only resolves to THIS machine - a Quest headset on the same wifi network needs " +
             "your machine's actual LAN IP here instead (e.g. http://192.168.1.23:5000) once " +
             "you're testing on-device rather than in the Editor/desktop simulator.")]
    public string baseUrl = "http://127.0.0.1:5000";

    [Tooltip("Portal results POST route. Leave blank until the portal supports the documented SessionRecord contract.")]
    public string sessionResultsPath = "";
}
