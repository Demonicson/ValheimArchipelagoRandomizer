using BepInEx;
using UnityEngine;
using Jotunn;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using System;
using System.Collections.Generic;
using HarmonyLib;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Xml;
using static ValheimRandomizer;
using static Version;

[BepInPlugin(ModGuid, ModName, ModVersion)]
[BepInDependency(Jotunn.Main.ModGuid)]

public class ValheimRandomizer : BaseUnityPlugin
{
    // Memory state caching
    public static readonly HashSet<String> UnlockedResearchCache = new(StringComparer.OrdinalIgnoreCase);
    public static readonly HashSet<String> CraftedResearchCache = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, string> PrefabToResearchMap = new(StringComparer.OrdinalIgnoreCase);

    private static readonly MethodInfo? UpdateRecipeListMethod = AccessTools.Method(typeof(InventoryGui), "UpdateRecipeList");

    // Call this when loading world, to initialize the cache before using it.
    public static void InitializeStateCache()
    {
        UnlockedResearchCache.Clear();
        CraftedResearchCache.Clear();

        if (ZoneSystem.instance == null) return; 

        // Populate cache on a world load from ZoneSystem
        foreach (var key in ZoneSystem.instance.GetGlobalKeys())
        {
            if (key.EndsWith("_crafted", StringComparison.OrdinalIgnoreCase))
            {
                CraftedResearchCache.Add(key.Substring(0, key.Length - "_crafted".Length));
            }
            else 
            {
                UnlockedResearchCache.Add(key);
            }
        }
    }

    public const string ModGuid = "com.samupo.randomizer";
    public const string ModName = "Randomizer";
    public const string ModVersion = "0.2.6";

    public static string Goal;

    internal static BepInEx.Configuration.ConfigEntry<bool> gatedResearch;
    internal static BepInEx.Configuration.ConfigEntry<bool> hideCompleted;
    internal static BepInEx.Configuration.ConfigEntry<bool> randomized;
    internal static BepInEx.Configuration.ConfigEntry<string> archipelagoHostname;
    internal static BepInEx.Configuration.ConfigEntry<int> archipelagoPort;
    internal static BepInEx.Configuration.ConfigEntry<string> archipelagoSlot;
    internal static BepInEx.Configuration.ConfigEntry<string> archipelagoPassword;



    public class TrophyResearch
    {
        public enum TrophyBoost { Load, HP, Stamina, Eitr, HPRegen, StaminaRegen, Attack, Defense, Adrenaline }

        public string itemID;
        public string researchID;
        public TrophyBoost boost;

        public TrophyResearch(string itemID, string researchID, TrophyBoost boost)
        {
            this.itemID = itemID;
            this.researchID = researchID;
            this.boost = boost;
        }
    }


    // Name for our custom piece/prefab that has a CraftingStation component
    public const string ResearchBenchPrefabName = "piece_randomizer_researchbench";

    public static CraftingStation ResearchBenchCraftingStation { get; set; }

    private Player currentPlayer = null;

    public static List<string> research = new List<string>();
    public static Dictionary<string, List<string>> recipeRequirements = new Dictionary<string, List<string>>();
    public static Dictionary<string, TrophyResearch> trophyByItemID = new Dictionary<string, TrophyResearch>();
    public static Dictionary<string, string> researchToArchipelago = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, string> archipelagoToResearch = new Dictionary<string, string>();

    public static BepInEx.Logging.ManualLogSource Log { get; private set; }

    float time = 0f;

    private void Awake()
    {
        gatedResearch = Config.Bind("Archipelago", "Enforce Research Prereqs", false, "Prevent out of logic checks at the research table. This behavior is from the original tiered research mod that the Valheim AP randomizer is based on. Disabling this allows any research to be completed as long as the materials have been found.");
        hideCompleted = Config.Bind("Archipelago", "Hide Completed Research", false, "Hide previously completed research items at the research bench");
        randomized = Config.Bind("Archipelago", "Randomized", false, "Uses Archipelago?");
        archipelagoHostname = Config.Bind("Archipelago", "Host", "localhost", "Archipelago server host");
        archipelagoPort = Config.Bind("Archipelago", "Port", 38281, "Archipelago server port");
        archipelagoSlot = Config.Bind("Archipelago", "Slot", "Player", "Your Archipelago slot name");
        archipelagoPassword = Config.Bind("Archipelago", "Password", "", "Archipelago password (if any)");

        ValheimRandomizer.Log = this.Logger;
        PrefabManager.OnVanillaPrefabsAvailable += DoOnPrefabsAvailable;
        new Harmony(ValheimRandomizer.ModGuid).PatchAll();

        var go = new GameObject("ValheimRandomizer.AP_UI");
        DontDestroyOnLoad(go);
        go.AddComponent<ArchipelagoUI>().Init();
    }

    void DoOnPrefabsAvailable()
    {
        RandomizerUtils.RegisterResearchBench();
        Invoke(nameof(AddResearchRecipes), 5.0f);
        Invoke(nameof(AddTrophyResearches), 5.5f);
        Invoke(nameof(CheckRecipesAndPiecesWithoutResearch), 10.0f);
    }

    private void Update()
    {
        if (currentPlayer != Player.m_localPlayer)
        {
            if (currentPlayer != null && currentPlayer.GetInventory() != null)
            {
                currentPlayer.GetInventory().m_onChanged -= OnInventoryChanged;
            }

            currentPlayer = Player.m_localPlayer;

            if (currentPlayer != null && currentPlayer.GetInventory() != null)
            {
                currentPlayer.GetInventory().m_onChanged += OnInventoryChanged;
                InitializeStateCache();
            }
        }

        time += Time.unscaledDeltaTime;
        if (time > 2.0f)
        {
            time = 0.0f;

            // Profile the 2-second network flush tick
            var sw = Stopwatch.StartNew(); // Debug
            ArchipelagoConnection.TryFlushPendingLocations();
            sw.Stop(); // Debug

            if (sw.Elapsed.TotalMilliseconds > 1.0) { DedicatedLogger.Log($"[PERF SPIKE] TryFlushPendingLocations() took {sw.Elapsed.TotalMilliseconds:F2} ms"); } // debug
        }
    }

    private void OnInventoryChanged() 
    { 
        if (currentPlayer == null || currentPlayer.GetInventory() == null) return;

        var sw = Stopwatch.StartNew(); // debug

        // Single-pass over current inventory to find an item whose "DROP PREFAB name matches our RESEARCH PREFAB ID"
        var items = currentPlayer.GetInventory().GetAllItems();
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            var dropName = item?.m_dropPrefab?.name; 
            if (string.IsNullOrEmpty(dropName)) continue;

            // Check against the goal condition selected by the user, in the .yml file for options.
            if (dropName == $"{Goal}")
            {
                ArchipelagoConnection.CompleteGame();
            }

            // Dictionary Lookup for Craftable Items (Consumed on pickup)
            if (PrefabToResearchMap.TryGetValue(dropName, out var researchId)) 
            { 
                if (!IsResearchCrafted(researchId)) 
                { 
                    UnlockResearch(researchId);
                    currentPlayer.GetInventory().RemoveItem(item, 1); 
                    break; // Remove 1 item per inventory update event
                }
            }
            
            // Trophy Passive Boosts (Unlocked on pickup, NOT consumed)
            if (trophyByItemID.TryGetValue(dropName, out var tr))
            {
                if (!IsResearchCrafted(tr.researchID))
                {
                    UnlockResearch(tr.researchID);
                    Log?.LogInfo($"[AP] Unlocked trophy stat research '{tr.researchID}' from '{dropName}'.");
                }
            }
        }

        sw.Stop(); if (sw.Elapsed.TotalMilliseconds > 1.0) { DedicatedLogger.Log($"[PERF SPIKE] OnInventoryChanged() took {sw.Elapsed.TotalMilliseconds:F2} ms"); } // debug
    }

    public static void DoUnlockResearch(string research)
    {
        if (string.IsNullOrEmpty(research)) return;

        UnlockedResearchCache.Add(research);

        if (ZoneSystem.instance != null)
        {

            ZoneSystem.instance.SetGlobalKey(research);
        }

        // Refresh the crafting workbench/forge UI if open
        if (InventoryGui.instance != null && UpdateRecipeListMethod != null) 
        { 
            try 
            { 
                UpdateRecipeListMethod.Invoke(InventoryGui.instance, null); 
            } catch (Exception ex) {
                Log?.LogWarning($"[AP] Could not refresh recipe list: {ex.Message}"); 
            } 
        }

        Log?.LogInfo($"[AP] Research unlocked: {research}");
    }
    public static void UnlockResearch(string research)
    {
        if (string.IsNullOrEmpty(research)) return; 
        
        CraftedResearchCache.Add(research);
        if (ZoneSystem.instance != null)
        {
            ZoneSystem.instance.SetGlobalKey(research + "_crafted");
        }
        if (randomized.Value)
        {
            ArchipelagoConnection.EnqueueLocationCheck(research);
        }
        else
        {
            DoUnlockResearch(research);
        }
    }

    public static bool IsResearchUnlocked(string research)
    {
        if (string.IsNullOrEmpty(research)) return false;
        return UnlockedResearchCache.Contains(research);
    }

    public static bool IsResearchCrafted(string research) 
    { 
        if (string.IsNullOrEmpty(research)) return false;
        return CraftedResearchCache.Contains(research); 
    }

    public static void AddResearchRequirement(string itemID, params string[] researches)
    {
        if (!recipeRequirements.ContainsKey(itemID))
        {
            recipeRequirements[itemID] = new List<string>();
        }
        foreach (string research in researches)
        {
            recipeRequirements[itemID].Add(research);
        }
    }

    public static IEnumerable<string> GetResearchRequired(string itemID)
    {
        if (!recipeRequirements.ContainsKey(itemID)) yield break;
        foreach (string research in recipeRequirements[itemID]) yield return research;
    }

    public static int GetBoost(TrophyResearch.TrophyBoost boost)
    {
        int total = 0;
        foreach (var tr in trophyByItemID.Values)
        {
            if (tr.boost != boost) continue;
            if (!IsResearchUnlocked(tr.researchID)) continue;
            total++;
        }
        return total;
    }

    private void AddResearchRecipes()
    {
        Logger.LogInfo("Trying to add recipes");
        if (!ResearchBenchCraftingStation)
        {
            Logger.LogWarning("AddResearchRecipes: Research bench prefab missing; skipping.");
            return;
        }

        try
        {
            string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string file = Path.Combine(modDir, "research.tsv");

            // Seed a tiny example if missing
            if (!File.Exists(file))
            {
                var seed = string.Join("\n", new[]
                {
                "# iconFromItemPrefab\tresearchItemID\tdisplayName\tdescription\trequiredResearchIDs\tresearchRecipeRequirements\tgatedItemPrefabIDs",
                "Hammer\tresearchWorkbench\tCrafting\tAllows basic crafting\t\tWood:5\tpiece_workbench",
                "Bow\tresearchBowTier1\tBows Tier 1\tAllows to craft bows and arrows\tresearchWorkbench\tWood:5|LeatherScraps:2\tBow|ArrowWood"
            });
                File.WriteAllText(file, seed);
                Logger.LogInfo($"Created starter research file at: {file}");
            }
            Logger.LogInfo("File found at " + file);

            int added = 0;
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                // Split on TABs (no escaping needed if you avoid tabs in text)
                var cols = line.Split('\t');
                if (cols.Length < 7)
                {
                    Logger.LogWarning($"Skipping line (needs 7 columns): {line}");
                    continue;
                }

                string iconSrc = cols[0];
                string rid = cols[1];
                string name = cols[2];
                string desc = cols[3];
                string prereqStr;
                if (gatedResearch.Value)
                { 
                    Log.LogInfo("Gating enabled: Adding " + cols[4] + " prereqs to " + cols[2]);
                    prereqStr = cols[4];
                }
                else
                {
                    Log.LogInfo("Gating disabled: Adding empty prereqs to " + cols[2]);
                    prereqStr = "";
                }
                string costsStr = cols[5];
                string gatedStr = cols[6];

                if (string.IsNullOrWhiteSpace(rid) || string.IsNullOrWhiteSpace(name))
                {
                    Logger.LogWarning($"Skipping line (missing id/name): {line}");
                    continue;
                }

                string[] prereqs = SplitPipes(prereqStr);
                string[] gated = SplitPipes(gatedStr);

                var reqs = new List<RequirementConfig>();
                foreach (var tok in SplitPipes(costsStr))
                {
                    var p = tok.Split(':');
                    if (p.Length == 2 && int.TryParse(p[1], out int amt) && amt > 0)
                        reqs.Add(new RequirementConfig(p[0], amt));
                }

                RandomizerUtils.CreateResearch(
                    string.IsNullOrWhiteSpace(iconSrc) ? "Amber" : iconSrc,
                    rid,
                    name,
                    desc ?? string.Empty,
                    prereqs,
                    reqs.ToArray(),
                    gated
                );

                added++;
            }
            foreach (var rid in research) { PrefabToResearchMap[rid] = rid; }
            Logger.LogInfo($"Loaded and registered {added} research definition(s) from {file}.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"AddResearchRecipes (from TSV) failed: {ex}");
        }

        // local helpers — tiny & clear
        string[] SplitPipes(string s)
            => string.IsNullOrWhiteSpace(s) ? Array.Empty<string>() : s.Split('|');
    }

    private void CheckRecipesAndPiecesWithoutResearch()
    {
        try
        {
            string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string recipesFile = Path.Combine(modDir, "RecipesWithoutResearch.txt");
            string piecesFile = Path.Combine(modDir, "PiecesWithoutResearch.txt");


            var recipesWithoutResearch = new List<string>();
            var piecesWithoutResearch = new List<string>();

            // --- Recipes: scan ObjectDB recipes
            if (ObjectDB.instance != null && ObjectDB.instance.m_recipes != null)
            {
                foreach (var recipe in ObjectDB.instance.m_recipes)
                {
                    if (recipe == null || recipe.m_item == null || !recipe.m_enabled) continue;
                    string id = recipe.m_item.name;
                    if (string.IsNullOrEmpty(id)) continue;

                    if (!ValheimRandomizer.recipeRequirements.ContainsKey(id))
                    {
                        string toAdd = id;
                        if (recipe.m_craftingStation != null)
                        {
                            toAdd += " " + recipe.m_craftingStation.name + " " + recipe.m_minStationLevel;
                        }
                        recipesWithoutResearch.Add(toAdd);
                    }
                }
            }

            // --- Pieces: gather all buildable pieces from every PieceTable
            var buildablePieces = new HashSet<Piece>();
            var allTables = Resources.FindObjectsOfTypeAll<PieceTable>();
            foreach (var table in allTables)
            {
                if (table?.m_pieces == null) continue;
                foreach (var p in table.m_pieces)
                {
                    if (p != null && p.GetComponent<Piece>() != null) buildablePieces.Add(p.GetComponent<Piece>());
                }
            }

            foreach (var piece in buildablePieces)
            {
                string id = piece?.name;
                if (string.IsNullOrEmpty(id)) continue;

                if (!ValheimRandomizer.recipeRequirements.ContainsKey(id))
                {
                    piecesWithoutResearch.Add(id);
                }
            }

            System.IO.File.WriteAllLines(recipesFile, recipesWithoutResearch);
            System.IO.File.WriteAllLines(piecesFile, piecesWithoutResearch);

            Logger.LogInfo($"Exported {recipesWithoutResearch.Count} recipes and {piecesWithoutResearch.Count} pieces without research.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error while exporting ungated recipes/pieces: {ex}");
        }
    }

    private void AddTrophyResearches()
    {
        try
        {
            string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string file = Path.Combine(modDir, "trophies.tsv");

            if (!System.IO.File.Exists(file))
            {
                Logger.LogWarning($"Trophy research file not found at {file}. Skipping trophy researches.");
                return;
            }

            int added = 0;

            foreach (var raw in System.IO.File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                var cols = line.Split('\t');
                if (cols.Length < 4)
                {
                    Logger.LogWarning($"Skipping trophy line (needs 4 columns): {line}");
                    continue;
                }

                string itemID = cols[0];
                string researchID = cols[1];
                string boostStr = cols[2];

                if (string.IsNullOrWhiteSpace(itemID) || string.IsNullOrWhiteSpace(researchID))
                {
                    Logger.LogWarning($"Skipping trophy line (missing itemID/researchID): {line}");
                    continue;
                }

                if (!TryParseBoost(boostStr, out TrophyResearch.TrophyBoost boost))
                {
                    Logger.LogWarning($"Unknown boost type '{boostStr}' for '{itemID}'. Defaulting to Load.");
                    boost = TrophyResearch.TrophyBoost.Load;
                }

                RandomizerUtils.CreateTrophyResearch(itemID, researchID, boost);
                added++;
            }

            Logger.LogInfo($"Loaded and registered {added} trophy research definition(s) from {file}.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"AddTrophyResearches (from TSV) failed: {ex}");
        }
    }

    static bool TryParseBoost(string s, out TrophyResearch.TrophyBoost boost)
    {
        return Enum.TryParse(s, out boost);
    }
}
