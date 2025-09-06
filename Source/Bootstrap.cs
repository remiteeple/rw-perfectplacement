using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace PerfectPlacement
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        public const string HarmonyId = "remi.PerfectPlacement";

        static Bootstrap()
        {
            try
            {
                var harmony = new Harmony(HarmonyId);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.Message("[PerfectPlacement] Harmony initialized and patches applied");
                try
                {
                    bool dbg = PerfectPlacement.Settings?.debugLogs ?? false;
                    Log.Message($"[PerfectPlacement] Debug logs {(dbg ? "ENABLED" : "disabled")} at startup.");
                }
                catch { }
            }
            catch (Exception e)
            {
                Log.Error($"[PerfectPlacement] Harmony patch failed: {e}");
            }
        }
    }
}
