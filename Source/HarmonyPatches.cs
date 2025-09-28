using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using UnityEngine;
using Verse.Sound;

namespace PerfectPlacement
{
    [HarmonyPatch]
    public static class Patch_Designator_Place_get_placingRot
    {
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;

            _isCached = true;
            var t = typeof(Designator_Place);

            var p = AccessTools.Property(t, "placingRot") ?? AccessTools.Property(t, "PlacingRot");
            var getter = p?.GetGetMethod(true);
            if (getter != null)
            {
                _cachedTarget = getter;
                return _cachedTarget;
            }

            var m = AccessTools.Method(t, "get_placingRot") ?? AccessTools.Method(t, "get_PlacingRot");
            if (m != null)
            {
                _cachedTarget = m;
                return _cachedTarget;
            }

            var bt = t.BaseType;
            while (bt != null)
            {
                var pb = AccessTools.Property(bt, "placingRot") ?? AccessTools.Property(bt, "PlacingRot");
                var getb = pb?.GetGetMethod(true);
                if (getb != null)
                {
                    _cachedTarget = getb;
                    return _cachedTarget;
                }

                var mb = AccessTools.Method(bt, "get_placingRot") ?? AccessTools.Method(bt, "get_PlacingRot");
                if (mb != null)
                {
                    _cachedTarget = mb;
                    return _cachedTarget;
                }

                bt = bt.BaseType;
            }

            return null;
        }

        static bool Prepare() => TargetMethod() != null;

        public static void Postfix(Designator_Place __instance, ref Rot4 __result)
        {
            try
            {
                // Only enforce architect build override here; avoid leaking into install/reinstall or other place flows.
                if (!(__instance is Designator_Build)) return;
                var s = PerfectPlacement.Settings;
                if (s == null) return;
                if (s.buildOverrideRotation != Rot4.South && Utilities.IsRotatable(__instance))
                {
                    var desired = s.buildOverrideRotation;
                    __result = desired;
                    Utilities.SetAllPlacingRotFields(__instance, desired);
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(DesignatorManager), nameof(DesignatorManager.Select))]
    public static class Patch_DesignatorManager_Select
    {
        public static void Postfix(Designator des)
        {
            Utilities.UnmarkApplied(des);
            Utilities.ClearPinned(des);
            Utilities.ClearRotatableCache();
            Utilities.ClearTransientAll();

            try
            {
                var settings = PerfectPlacement.Settings;
                if (settings?.PerfectPlacement == true && des != null && !settings.useOverrideRotation)
                {
                    if (Utilities.IsReinstallDesignator(des, out var source) && source != null && Utilities.IsRotatable(des))
                    {
                        var desired = source.Rotation;
                        if (Utilities.SetAllPlacingRotFields(des, desired))
                        {
                            Utilities.MarkApplied(des);
                        }
                    }
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(DesignatorManager), nameof(DesignatorManager.ProcessInputEvents))]
    public static class Patch_DesignatorManager_ProcessInputEvents_MouseRotate
    {
        public static bool Prefix(DesignatorManager __instance)
        {
            try
            {
                if (Utilities.HandleProcessInputEvents(__instance))
                {
                    return false;
                }
            }
            catch (Exception e)
            {
                Log.Warning("[PerfectPlacement] ProcessInputEvents prefix failed: " + e.Message);
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(DesignatorManager), nameof(DesignatorManager.DesignatorManagerUpdate))]
    public static class Patch_DesignatorManager_DesignatorManagerUpdate_MouseRotate
    {
        public static void Postfix(DesignatorManager __instance)
        {
            try
            {
                Utilities.HandleDesignatorUpdate(__instance?.SelectedDesignator);
            }
            catch (Exception e)
            {
                Log.Warning("[PerfectPlacement] DesignatorManagerUpdate postfix failed: " + e.Message);
            }
        }
    }
    [HarmonyPatch(typeof(UI), nameof(UI.MouseCell))]
    public static class Patch_UI_MouseCell_PinDuringRotate
    {
        public static bool Prefix(ref IntVec3 __result)
        {
            var settings = PerfectPlacement.Settings;
            if (settings == null) return true;
            if (Utilities.SuppressMouseCellPin) return true;
            if (!Utilities.HasAnyPinned) return true;
            try
            {
                var des = Find.DesignatorManager?.SelectedDesignator ?? Utilities.CurrentSelectedDesignator();
                if (des == null) return true;
                if (!Utilities.MouseRotateEnabledFor(des, settings) || !Utilities.IsRotatable(des)) return true;
                if (Utilities.TryGetPinned(des, out var pin))
                {
                    __result = pin;
                    return false;
                }
            }
            catch { }
            return true;
        }
    }


}
