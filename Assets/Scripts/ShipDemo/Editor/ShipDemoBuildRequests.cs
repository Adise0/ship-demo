using System.IO;
using UnityEditor;

/// <summary>
/// Lets build-server.sh build through an editor that already has the project open, since a
/// batch-mode Unity can't open the project then. The script drops a request file in
/// Temp/ShipDemoBuild; this builds the server and writes the result next to it.
/// </summary>
[InitializeOnLoad]
public static class ShipDemoBuildRequests
{
    private const string Folder = "Temp/ShipDemoBuild";
    private const string RequestPath = Folder + "/server.request";
    private const string ResultPath = Folder + "/server.result";

    private static double _nextCheckTime;

    static ShipDemoBuildRequests()
    {
        EditorApplication.update += Poll;
    }

    private static void Poll()
    {
        if (EditorApplication.timeSinceStartup < _nextCheckTime)
            return;
        _nextCheckTime = EditorApplication.timeSinceStartup + 0.5;

        if (!File.Exists(RequestPath) || EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;

        // Can't build in Play mode. Leave the request in place and build once Play mode has exited.
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            if (EditorApplication.isPlaying)
            {
                UnityEngine.Debug.Log("Exiting Play mode to build the dedicated server.");
                EditorApplication.isPlaying = false;
            }
            return;
        }
        File.Delete(RequestPath);

        string result = ShipDemoBuild.TryBuildServer() ? "succeeded" : "failed: see the build log";

        // Write then move, so the script never reads a half-written result.
        string tempPath = ResultPath + ".tmp";
        File.WriteAllText(tempPath, result);
        File.Delete(ResultPath);
        File.Move(tempPath, ResultPath);
    }
}
