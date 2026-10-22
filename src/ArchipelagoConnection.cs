using System;
using System.Linq;
using UnityEngine.Events;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Helpers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;



internal static class ArchipelagoConnection
{
    private static ArchipelagoSession session;
    private static bool connected;

    // In-memory queue for pending location checks (Thread-safe)
    private static readonly ConcurrentQueue<string> PendingLocationQueue = new();

    // Cache processed item IDs to prevent duplicate unlocks during catch-up when reconnecting
    private static readonly HashSet<string> ProcessedReceivedItems = new(StringComparer.OrdinalIgnoreCase);

    public static void Connect(string host, int port, string slot, string password)
    {
        // Dispose previous session if any
        session?.Socket?.DisconnectAsync();
        session = null;
        connected = false;
        ProcessedReceivedItems.Clear();

        // Create & login
        session = ArchipelagoSessionFactory.CreateSession(host, port);
        var result = session.TryConnectAndLogin("Valheim", slot,
            ItemsHandlingFlags.AllItems, password: password);

        if (!result.Successful)
        {
            // Throw the first error up; UI handles it and shows the message
            var failure = (LoginFailure)result;
            throw new Exception(string.Join("; ", failure.Errors ?? Array.Empty<string>()));
        }


        if (result.Successful)
        {
           var loginSuccess = (LoginSuccessful)result;
           if (loginSuccess.SlotData.TryGetValue("goal", out var goal_string))
           {
             ValheimRandomizer.Goal = Convert.ToString(goal_string);
           }
        }

        // Hook item reception event (Fires automatically when items are sent by the server)
        session.Items.ItemReceived += OnApItemReceived;
        connected = true;

        // Process any items that were sent while player was offline (Runs ONCE on "Connect)
        ProcessCatchUpItems();
    }

    /// <summary> 
    /// "Enqueues" a research ID to be sent to Archipelago as a completed check. 
    /// </summary>
    public static void EnqueueLocationCheck(string researchId)
    {
        if (!ValheimRandomizer.randomized.Value) return;
        if (string.IsNullOrWhiteSpace(researchId)) return;
        PendingLocationQueue.Enqueue(researchId); // Flush immediately if connected
        TryFlushPendingLocations(); 
    }

    /// <summary> 
    /// Event callback when a new item is received from Archipelago server.
    /// </summary>
    private static void OnApItemReceived(ReceivedItemsHelper helper)
    {
        if (!WorldLoaded()) return;
        while (true) 
        { 
            var item = helper.DequeueItem();
            if (item == null) break;
            ProcessItem(item);
        }
    }

    /// <summary> 
    /// Syncs items received while offline (Runs ONCE during session connection). 
    /// </summary>
    private static void ProcessCatchUpItems()
    {
        if (session?.Items?.AllItemsReceived == null) return;
        foreach (var item in session.Items.AllItemsReceived) { ProcessItem(item); }

        /* 
           if (!WorldLoaded() == null) return; <- Commented out because WorldLoaded() is false when connecting from the main menu specifically.
           Leaving the above here as a reminder, either don't check it here- or ensure ProcessCatchUpItems() is called when spawning into the world
           inside of InitalizeStateCache() in ValheimRandomizer.cs ... 
        */
    }

    private static void ProcessItem(ItemInfo item)
    {
        var name = item.ItemName;
        if (string.IsNullOrEmpty(name)) return;

        // Ensure each received item instance is processed
        var itemKey = $"{item.Player}_{item.LocationId}_{name}";

        if (ProcessedReceivedItems.Contains(itemKey)) return;
        ProcessedReceivedItems.Add(itemKey);

        var senderID = item.Player;
        string sender = session.Players.GetPlayerAliasAndName(senderID) ?? "Server";

        if (ValheimRandomizer.archipelagoToResearch.TryGetValue(name, out var researchId))
        {
            if (MessageHud.instance != null)
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"Received {name} from {sender}!");
            }
            ValheimRandomizer.DoUnlockResearch(researchId);
        }
        else
        {
            ValheimRandomizer.Log?.LogWarning($"[AP] Received item '{name}' but no research mapping was found.");
        }
    }

    /// <summary> 
    /// Flushes queued location checks asynchronously from memory. 
    /// </summary>
    public static void TryFlushPendingLocations()
    {
        if (!connected || session == null || !WorldLoaded()) return;

        var sw = Stopwatch.StartNew(); //debug
        int processedCount = 0; //debug

        while (PendingLocationQueue.TryDequeue(out var researchId))
        {
            processedCount++; // debug
            // Map research ID to Archipelago location name
            if (!ValheimRandomizer.researchToArchipelago.TryGetValue(researchId, out var locationName)) 
            { 
                locationName = researchId; 
            }
            try 
            {
                var locationId = session.Locations.GetLocationIdFromName("Valheim", locationName);
                if (locationId != -1) 
                { 
                    session.Locations.CompleteLocationChecks(locationId); 
                    ValheimRandomizer.Log?.LogInfo($"[AP] Successfully checked location: {locationName} (ID: {locationId})");
                } 
                else { ValheimRandomizer.Log?.LogWarning($"[AP] Location name '{locationName}' not found in DataPackage."); }
            }
            catch (Exception ex)
            {
                // Re-queue for next retry tick if network request fails
                PendingLocationQueue.Enqueue(researchId);
                ValheimRandomizer.Log?.LogWarning($"[AP] Failed to send location '{locationName}': {ex.Message}"); 
                break; // Stop processing queue for this tick if network error occurred
            }
        }

        sw.Stop(); if (sw.Elapsed.TotalMilliseconds > 1.0) { DedicatedLogger.Log($"[PERF SPIKE] Network Queue flush ({processedCount} items) took {sw.Elapsed.TotalMilliseconds:F2} ms"); } //debug
    }

    private static bool WorldLoaded()
        => ZoneSystem.instance != null && Player.m_localPlayer != null; // simple & reliable

    public static void CompleteGame()
    {
        if (connected && session != null)
        {
            session.SetGoalAchieved();
            ValheimRandomizer.Log?.LogInfo("[AP] Victory condition met! Goal completed.");
        }
    }
}
