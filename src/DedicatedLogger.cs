using System;
using System.IO;
using System.Reflection;

public static class DedicatedLogger 
{ 
    private static readonly string LogFilePath;
    private static readonly object LockObj = new object();

    static DedicatedLogger()
    {
        // Save log file in the exact same directory as ValheimRandomizer.dll
        string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory;
        LogFilePath = Path.Combine(modDir, "ValheimRandomizer_Perf.log");

        // Overwrite/clear previous log session on game start
        try
        {
            File.WriteAllText(LogFilePath, $"=== Valheim Randomizer Log Started: {DateTime.Now} ===\n");
        }
        catch { /* Prevent crashes if file is locked \*/ }
    }

    public static void Log(string message) 
    { 
        lock (LockObj) 
        { 
            try 
            { 
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
                File.AppendAllText(LogFilePath, line); 
            } catch { /* Ignore write errors to prevent game frame drops */ } 
        } 
    } 
}