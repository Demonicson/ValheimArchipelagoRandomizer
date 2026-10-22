using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Windows;

/// <summary>
/// Archipelago Connection UI component for Valheim Main Menu.
/// Optimized to use event-driven scene checking to eliminate per-frame scene graph searching done in prior versions.
/// </summary>
public class ArchipelagoUI : MonoBehaviour
{
    // UI state
    private Rect _win = new Rect(30, 80, 420, 260);
    private bool _randomized;
    private string _host = string.Empty;
    private string _slot = string.Empty;
    private string _password = string.Empty;
    private int _port = 38281; // default

    // Connection state
    private string _status = "Not connected";
    private bool _connecting = false;
    private bool _inMainMenu = false;

    public void Init()
    {
        // Load config values into fields
        if (ValheimRandomizer.randomized != null)
            _randomized = ValheimRandomizer.randomized.Value;
        if (ValheimRandomizer.archipelagoHostname != null)
            _host = ValheimRandomizer.archipelagoHostname.Value ?? string.Empty;
        if (ValheimRandomizer.archipelagoPort != null)
            _port = ValheimRandomizer.archipelagoPort.Value;
        if (ValheimRandomizer.archipelagoSlot != null)
            _slot = ValheimRandomizer.archipelagoSlot.Value ?? string.Empty;
        if (ValheimRandomizer.archipelagoPassword != null)
            _password = ValheimRandomizer.archipelagoPassword.Value ?? string.Empty;

        // Hook scene change events to avoid frame polling
        SceneManager.sceneLoaded += OnSceneLoaded;
        CheckActiveScene();
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        CheckActiveScene();
    }

    private void CheckActiveScene()
    {
        // Valheim's main menu scene is named "start" so for now we're hard-coding it.
        string sceneName = SceneManager.GetActiveScene().name;
        _inMainMenu = string.Equals(sceneName, "start", StringComparison.OrdinalIgnoreCase);
    }

    private void OnGUI()
    {
        if (!_inMainMenu) return;

        // Pause UI scaling for different resolutions
        var oldColor = GUI.color;
        GUI.color = Color.white;

        _win = GUILayout.Window(
            GetInstanceID(),
            _win,
            DrawWindow,
            "Archipelago – Valheim Randomizer",
            GUILayout.ExpandHeight(true),
            GUILayout.ExpandWidth(true)
        );

        GUI.color = oldColor;
    }

    private void DrawWindow(int id)
    {
        GUILayout.BeginVertical();

        GUILayout.Label("Connect to an Archipelago room to enable item/location randomization.");
        GUILayout.Space(6);

        _randomized = GUILayout.Toggle(_randomized, "Randomized?");

        GUILayout.BeginHorizontal();
        GUILayout.Label("Host", GUILayout.Width(60));
        _host = GUILayout.TextField(_host ?? string.Empty, GUILayout.Width(220));
        GUILayout.Label("Port", GUILayout.Width(40));
        var portStr = GUILayout.TextField((_port <= 0 ? "" : _port.ToString()), GUILayout.Width(70));
        if (int.TryParse(portStr, out var p)) _port = p;
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Slot", GUILayout.Width(60));
        _slot = GUILayout.TextField(_slot ?? string.Empty, GUILayout.Width(140));
        GUILayout.Label("Password", GUILayout.Width(80));
        _password = GUILayout.PasswordField(_password ?? string.Empty, '*', GUILayout.Width(140));
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        GUILayout.Label($"Status: {_status}");
        GUILayout.Space(6);

        GUILayout.BeginHorizontal();

        bool clientConnected = false;
        GUI.enabled = !_connecting && !clientConnected && ValidInputs();
        if (GUILayout.Button("Connect", GUILayout.Height(28)))
        {
            SaveInputs();
            StartConnect();
        }
        GUI.enabled = clientConnected && !_connecting;
        if (GUILayout.Button("Disconnect", GUILayout.Height(28)))
        {
            Disconnect();
        }
        GUI.enabled = true;

        GUILayout.FlexibleSpace();

        GUILayout.EndHorizontal();

        GUILayout.Space(6);

        GUILayout.EndVertical();

        GUI.DragWindow(new Rect(0, 0, 10000, 20));
    }

    private bool ValidInputs()
    {
        return !string.IsNullOrWhiteSpace(_host)
            && _port > 0 && _port < 65536
            && !string.IsNullOrWhiteSpace(_slot);
    }

    private void SaveInputs()
    {
        if (ValheimRandomizer.randomized != null)
            ValheimRandomizer.randomized.Value = _randomized;
        if (ValheimRandomizer.archipelagoHostname != null)
            ValheimRandomizer.archipelagoHostname.Value = _host ?? string.Empty;
        if (ValheimRandomizer.archipelagoPort != null)
            ValheimRandomizer.archipelagoPort.Value = _port;
        if (ValheimRandomizer.archipelagoSlot != null)
            ValheimRandomizer.archipelagoSlot.Value = _slot ?? string.Empty;
        if (ValheimRandomizer.archipelagoPassword != null)
            ValheimRandomizer.archipelagoPassword.Value = _password ?? string.Empty;
    }

    private void StartConnect()
    {
        _connecting = true;
        _status = "Connecting...";

        try
        {
            ArchipelagoConnection.Connect(_host, _port, _slot, _password);
            _status = "Connected";
        }
        catch (Exception ex)
        {
            _status = $"Error: {ex.Message}";
            ValheimRandomizer.Log?.LogError($"[AP] Connect error: {ex}");
        }
        finally
        {
            _connecting = false;
        }
    }

    private void Disconnect()
    {
        _status = "Not connected";
    }
}
