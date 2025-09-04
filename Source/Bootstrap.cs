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
            }
            catch (Exception e)
            {
                Log.Error($"[PerfectPlacement] Harmony patch failed: {e}");
            }
        }
    }
}
