using System;
using FishNet.Managing;
using UnityEngine;

/// <summary>
/// Starts networking from command-line arguments, so a build can run as a dedicated server
/// (or client) without clicking the HUD. Usage: Game.exe -server   /   Game.exe -client
/// Dedicated Server builds always start the server.
/// </summary>
public class DemoAutoStart : MonoBehaviour
{
    private void Start()
    {
        string[] args = Environment.GetCommandLineArgs();
        // The editor can compile with UNITY_SERVER when its build target is Dedicated Server;
        // that must not turn Play mode into a server.
#if UNITY_SERVER && !UNITY_EDITOR
        bool server = true;
#else
        bool server = Array.IndexOf(args, "-server") >= 0;
#endif
        bool client = Array.IndexOf(args, "-client") >= 0;
        if (!server && !client)
            return;

        NetworkManager networkManager = GetComponent<NetworkManager>();
        if (server)
        {
            // A headless server doesn't need to render or run fast frames.
            Application.targetFrameRate = 120;
            networkManager.ServerManager.StartConnection();
        }
        if (client)
            networkManager.ClientManager.StartConnection();
    }
}
