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

    [HarmonyPatch]
    public static class Patch_Designator_DesignateSingleCell_MouseRotate_All
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var targets = new[] { typeof(Designator_Install), typeof(Designator_Place), typeof(Designator_Build) };
            foreach (var t in targets)
            {
                var m = FindDesignateSingleCell(t);
                if (m != null) yield return m;
            }
        }

        private static MethodInfo FindDesignateSingleCell(Type t)
        {
            var intVec3 = typeof(IntVec3);
            var direct = AccessTools.Method(t, "DesignateSingleCell", new[] { intVec3 });
            if (direct != null) return direct;

            foreach (var mi in AccessTools.GetDeclaredMethods(t))
            {
                if (!mi.Name.Contains("Designate")) continue;
                var pars = mi.GetParameters();
                if (pars.Length == 1 && pars[0].ParameterType == intVec3 && mi.ReturnType == typeof(void))
                {
                    return mi;
                }
            }

            var bt = t.BaseType;
            while (bt != null)
            {
                var m = AccessTools.Method(bt, "DesignateSingleCell", new[] { intVec3 });
                if (m != null) return m;

                foreach (var mi in AccessTools.GetDeclaredMethods(bt))
                {
                    if (!mi.Name.Contains("Designate")) continue;
                    var pars = mi.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == intVec3 && mi.ReturnType == typeof(void))
                    {
                        return mi;
                    }
                }
                bt = bt.BaseType;
            }
            return null;
        }

        static bool Prepare() => TargetMethods().Any();

        public static bool Prefix(Designator __instance)
        {
            return Utilities.HandleDesignatePrefix(__instance);
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
                var des = Utilities.CurrentSelectedDesignator();
                if (!(des is Designator_Install) && !(des is Designator_Place) && !(des is Designator_Build)) return true;
                bool mouseRotateEnabled = Utilities.MouseRotateEnabledFor(des, settings);
                if (!mouseRotateEnabled || !Utilities.IsRotatable(des)) return true;
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

    [HarmonyPatch(typeof(DesignatorManager), nameof(DesignatorManager.Select))]
    public static class Patch_DesignatorManager_Select
    {
        public static void Postfix(Designator des)
        {
            Utilities.UnmarkApplied(des);
            Utilities.ClearPinned(des);
            Utilities.ClearRotatableCache();
            Utilities.ClearTransientAll();
        }
    }

    [HarmonyPatch(typeof(Designator_Install), nameof(Designator_Install.SelectedUpdate))]
    public static class Patch_Designator_Install_SelectedUpdate
    {
        public static void Prefix(Designator __instance)
        {
            if (__instance == null) return;
            Utilities.ConsumeLeftClickIfMouseRotate(__instance);
        }

        public static void Postfix(Designator __instance)
        {
            if (__instance == null) return;
            try
            {
                var settings = PerfectPlacement.Settings;
                if (settings == null) return;

                bool isReinstall = Utilities.IsReinstallDesignator(__instance, out var source);

                bool anyActive = isReinstall
                    ? (settings.useOverrideRotation || settings.PerfectPlacement)
                    : (settings.installOverrideRotation != Rot4.South);
                if (!anyActive) return;

                if (Utilities.ApplyInstallOrReinstallOverrideIfNeeded(__instance, settings, isReinstall)) return;

                if (isReinstall && settings.PerfectPlacement && !Utilities.WasApplied(__instance))
                {
                    if (Utilities.IsRotatable(__instance))
                    {
                        var desiredKeep = source.Rotation;
                        if (Utilities.SetAllPlacingRotFields(__instance, desiredKeep)) Utilities.MarkApplied(__instance);
                        else Utilities.DebugLog(() => $"KeepRotation: failed to set placingRot for {__instance.GetType().Name}");
                    }
                    else
                    {
                        Utilities.DebugLog(() => $"KeepRotation: not rotatable or null source. src={(source==null?"<null>":source.def.defName)}");
                    }
                }
                if (Utilities.HandleMouseRotate(__instance)) return;
            }
            catch (Exception e)
            {
                Log.Warning("[PerfectPlacement] Install.SelectedUpdate failed: " + e.Message);
            }
        }
    }

    

    [HarmonyPatch(typeof(Designator_Place), nameof(Designator_Place.SelectedUpdate))]
    public static class Patch_Designator_Place_SelectedUpdate
    {
        public static void Prefix(Designator __instance)
        {
            if (__instance == null) return;
            Utilities.ConsumeLeftClickIfMouseRotate(__instance);
        }
        public static void Postfix(Designator __instance)
        {
            if (__instance == null) return;
            try
            {
                var settings = PerfectPlacement.Settings;
                if (settings == null) return;

                bool buildActive = (__instance is Designator_Build) && (settings.buildOverrideRotation != Rot4.South);
                bool anyActive = buildActive || Utilities.MouseRotateEnabledFor(__instance, settings);
                if (!anyActive) return;

                if (__instance is Designator_Build)
                {
                    Utilities.ApplyBuildOverrideIfNeeded(__instance, settings);
                }
                if (Utilities.HandleMouseRotate(__instance)) return;
            }
            catch (Exception e)
            {
                Log.Warning("[PerfectPlacement] Place.SelectedUpdate failed: " + e.Message);
            }
        }
    }

    

    [HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.SelectedUpdate))]
    public static class Patch_Designator_Build_SelectedUpdate
    {
        public static void Prefix(Designator __instance)
        {
            if (__instance == null) return;
            Utilities.ConsumeLeftClickIfMouseRotate(__instance);
        }
        public static void Postfix(Designator __instance)
        {
            if (__instance == null) return;
            try
            {
                var settings = PerfectPlacement.Settings;
                if (settings == null) return;

                bool anyActive = (settings.buildOverrideRotation != Rot4.South) || Utilities.MouseRotateEnabledFor(__instance, settings);
                if (!anyActive) return;

                Utilities.ApplyBuildOverrideIfNeeded(__instance, settings);
                if (Utilities.HandleMouseRotate(__instance)) return;
            }
            catch (Exception e)
            {
                Log.Warning("[PerfectPlacement] Build.SelectedUpdate failed: " + e.Message);
            }
        }
    }

    

    [HarmonyPatch]
    public static class Patch_Designator_ProcessInput_MouseRotate
    {
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;

            _isCached = true;
            var t = typeof(Designator);
            var m = AccessTools.Method(t, "ProcessInput", new[] { typeof(Event) });
            if (m != null)
            {
                _cachedTarget = m;
                return _cachedTarget;
            }
            foreach (var mi in AccessTools.GetDeclaredMethods(t))
            {
                if (mi.Name != "ProcessInput") continue;
                var pars = mi.GetParameters();
                if (pars.Length == 1 && pars[0].ParameterType == typeof(Event) && mi.ReturnType == typeof(void))
                {
                    _cachedTarget = mi;
                    return _cachedTarget;
                }
            }
            var bt = t.BaseType;
            while (bt != null)
            {
                m = AccessTools.Method(bt, "ProcessInput", new[] { typeof(Event) });
                if (m != null)
                {
                    _cachedTarget = m;
                    return _cachedTarget;
                }
                foreach (var mi in AccessTools.GetDeclaredMethods(bt))
                {
                    if (mi.Name != "ProcessInput") continue;
                    var pars = mi.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(Event) && mi.ReturnType == typeof(void))
                    {
                        _cachedTarget = mi;
                        return _cachedTarget;
                    }
                }
                bt = bt.BaseType;
            }
            return null;
        }

        static bool Prepare() => TargetMethod() != null;

        public static bool Prefix(Designator __instance, Event ev)
        {
            return Utilities.HandleProcessInputPrefix(__instance, ev);
        }
    }

    [HarmonyPatch(typeof(DesignatorManager), nameof(DesignatorManager.ProcessInputEvents))]
    public static class Patch_DesignatorManager_ProcessInputEvents_SuppressWhilePinned
    {
        public static bool Prefix()
        {
            try
            {
                var evt = Event.current;
                var des = Utilities.CurrentSelectedDesignator();
                var settings = PerfectPlacement.Settings;

                Utilities.SetSuppressRejectsThisEvent(false);

                if (evt != null && evt.type == EventType.MouseDown && evt.button == 0 && des != null && settings != null)
                {
                    if (Utilities.MouseRotateEnabledFor(des, settings) && Utilities.IsRotatable(des))
                    {
                        Utilities.SetSuppressRejectsThisEvent(true);
                    }
                }

                if (Utilities.HasAnyPinned && des != null && settings != null)
                {
                    if (Utilities.MouseRotateEnabledFor(des, settings) && evt != null && evt.type == EventType.MouseDown && evt.button == 0)
                    {
                        evt.Use();
                        return false;
                    }
                }
            }
            catch { }
            return true;
        }
    }

    [HarmonyPatch(typeof(Messages))]
    public static class Patch_Messages_Message_Suppress_All
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            // Dynamically target all Messages.Message overloads (public/non-public, static)
            return typeof(Messages)
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(mi => mi.Name == nameof(Messages.Message));
        }

        static bool Prepare() => TargetMethods().Any();

        public static bool Prefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0) return true;

                MessageTypeDef def = null;
                TaggedString text = default;
                bool haveText = false;

                foreach (var arg in __args)
                {
                    if (!haveText)
                    {
                        if (arg is string s)
                        {
                            text = s;
                            haveText = true;
                            continue;
                        }
                        if (arg is TaggedString ts)
                        {
                            text = ts;
                            haveText = true;
                            continue;
                        }
                    }
                    if (def == null && arg is MessageTypeDef md)
                    {
                        def = md;
                    }
                }

                if (!haveText || def == null) return true;
                if (Utilities.ShouldSuppressDesignatorReject(text, def)) return false;
            }
            catch { }
            return true;
        }
    }
}
