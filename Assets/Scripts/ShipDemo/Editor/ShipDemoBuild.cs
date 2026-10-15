using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Builds for the ship demo. Usable from the menu or the command line:
///   Unity.exe -batchmode -quit -projectPath "C:\Projects\Unity\ship-demo" -executeMethod ShipDemoBuild.BuildServer -logFile -
/// </summary>
public static class ShipDemoBuild
{
    private static readonly string[] Scenes = { "Assets/ShipDemo/ShipDemo.unity" };

    /// <summary>
    /// Dedicated Server build: a headless console app that starts the server on launch
    /// and stops with Ctrl+C.
    /// </summary>
    [MenuItem("Tools/Ship Demo/Build Dedicated Server")]
    public static void BuildServer() => TryBuildServer();

    /// <returns>True if the build succeeded.</returns>
    internal static bool TryBuildServer()
    {
        return Build(new BuildPlayerOptions
        {
            scenes = Scenes,
            locationPathName = "Builds/ShipDemoServer/ShipDemoServer.exe",
            target = BuildTarget.StandaloneWindows64,
            subtarget = (int)StandaloneBuildSubtarget.Server,
            options = BuildOptions.Development,
        });
    }

    /// <summary>
    /// Regular windowed player, e.g. to run extra clients (ShipDemo.exe -client).
    /// </summary>
    [MenuItem("Tools/Ship Demo/Build Client")]
    public static void BuildClient()
    {
        Build(new BuildPlayerOptions
        {
            scenes = Scenes,
            locationPathName = "Builds/ShipDemo/ShipDemo.exe",
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development,
        });
    }

    private static bool Build(BuildPlayerOptions options)
    {
        // BuildPlayer switches the editor's own subtarget to match the build. A Server subtarget
        // makes the editor compile with UNITY_SERVER, so put it back afterward.
        StandaloneBuildSubtarget previousSubtarget = EditorUserBuildSettings.standaloneBuildSubtarget;
        BuildReport report = BuildPipeline.BuildPlayer(options);
        EditorUserBuildSettings.standaloneBuildSubtarget = previousSubtarget;

        BuildSummary summary = report.summary;
        if (summary.result == BuildResult.Succeeded)
        {
            Debug.Log($"Built {summary.outputPath} in {summary.totalTime:mm\\:ss}.");
            return true;
        }

        Debug.LogError($"Build {summary.result}: {summary.totalErrors} error(s). See the log above.");
        // Non-zero exit code so command-line builds can detect failure.
        if (Application.isBatchMode)
            EditorApplication.Exit(1);
        return false;
    }
}
