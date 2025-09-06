using System;
using System.Reflection;
using System.Collections.Generic;
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
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = typeof(Designator_Place);

            // Try direct property lookup first on this type
            var p = AccessTools.Property(t, "placingRot") ?? AccessTools.Property(t, "PlacingRot");
            var getter = p?.GetGetMethod(true);
            if (getter != null) return getter;

            // Fallback: look for explicit getter names on this type
            var m = AccessTools.Method(t, "get_placingRot") ?? AccessTools.Method(t, "get_PlacingRot");
            if (m != null) return m;

            // As a last resort, walk base types (unlikely but safe)
            var bt = t.BaseType;
            while (bt != null)
            {
                var pb = AccessTools.Property(bt, "placingRot") ?? AccessTools.Property(bt, "PlacingRot");
                var getb = pb?.GetGetMethod(true);
                if (getb != null) return getb;

                var mb = AccessTools.Method(bt, "get_placingRot") ?? AccessTools.Method(bt, "get_PlacingRot");
                if (mb != null) return mb;

                bt = bt.BaseType;
            }

            return null; // Handled by Prepare() to skip cleanly
        }

        // Skip patch entirely if getter is unavailable in this game version
        static bool Prepare()
        {
            return TargetMethod() != null;
        }

        public static void Postfix(Designator_Place __instance, ref Rot4 __result)
        {
            try
            {
                if (__instance is Designator_Install) return;
                var s = PerfectPlacement.Settings;
                if (s == null) return;
                if (s.buildUseOverrideRotation && Utilities.IsRotatable(__instance))
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
    public static class Patch_Designator_Install_DesignateSingleCell_MouseRotate
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = typeof(Designator_Install);
            // Prefer exact signature
            var m = AccessTools.Method(t, "DesignateSingleCell", new[] { typeof(IntVec3) });
            if (m != null) return m;
            // Fallback: any void method with IntVec3 parameter containing 'Designate'
            foreach (var mi in AccessTools.GetDeclaredMethods(t))
            {
                if (!mi.Name.Contains("Designate")) continue;
                var pars = mi.GetParameters();
                if (pars.Length == 1 && pars[0].ParameterType == typeof(IntVec3) && mi.ReturnType == typeof(void))
                    return mi;
            }
            // Walk base types
            var bt = t.BaseType;
            while (bt != null)
            {
                m = AccessTools.Method(bt, "DesignateSingleCell", new[] { typeof(IntVec3) });
                if (m != null) return m;
                foreach (var mi in AccessTools.GetDeclaredMethods(bt))
                {
                    if (!mi.Name.Contains("Designate")) continue;
                    var pars = mi.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(IntVec3) && mi.ReturnType == typeof(void))
                        return mi;
                }
                bt = bt.BaseType;
            }
            return null;
        }

        static bool Prepare() => TargetMethod() != null;

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
            // Allow callers to bypass pinning (e.g., when computing rotation from real cursor)
            if (Utilities.SuppressMouseCellPin) return true;
            // Fast early-out: if nothing is pinned, no override needed
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
            // Reset initial-apply flag on selection so a reused designator gets a fresh initial rotation
            Utilities.UnmarkApplied(des);
            Utilities.ClearPinned(des);
            Utilities.ClearRotatableCache();
            Utilities.ClearTransientAll();
        }
    }

    [HarmonyPatch(typeof(Designator_Install), nameof(Designator_Install.SelectedUpdate))]
    public static class Patch_Designator_Install_SelectedUpdate
    {
        // Early: consume MouseDown so base doesn't place/sound on press
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
                    : settings.installUseOverrideRotation;
                if (!anyActive) return;

                // Apply override once (Install/Reinstall); return early if applied
                if (Utilities.ApplyInstallOrReinstallOverrideIfNeeded(__instance, settings, isReinstall)) return;

                // PerfectPlacement: set initial rotation once, only for REINSTALL
                if (isReinstall && settings.PerfectPlacement && !Utilities.WasApplied(__instance))
                {
                    // Only apply keep-rotation when the source is rotatable
                    bool rotatable = (source?.def as ThingDef)?.rotatable ?? false;
                    if (rotatable)
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
                // Sims-like mouse rotation
                if (Utilities.HandleMouseRotate(__instance)) return;
            }
            catch (Exception e)
            {
                Log.Warning("[PerfectPlacement] Install.SelectedUpdate failed: " + e.Message);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_Designator_Place_DesignateSingleCell_MouseRotate
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = typeof(Designator_Place);
            var m = AccessTools.Method(t, "DesignateSingleCell", new[] { typeof(IntVec3) });
            if (m != null) return m;
            foreach (var mi in AccessTools.GetDeclaredMethods(t))
            {
                if (!mi.Name.Contains("Designate")) continue;
                var pars = mi.GetParameters();
                if (pars.Length == 1 && pars[0].ParameterType == typeof(IntVec3) && mi.ReturnType == typeof(void))
                    return mi;
            }
            var bt = t.BaseType;
            while (bt != null)
            {
                m = AccessTools.Method(bt, "DesignateSingleCell", new[] { typeof(IntVec3) });
                if (m != null) return m;
                foreach (var mi in AccessTools.GetDeclaredMethods(bt))
                {
                    if (!mi.Name.Contains("Designate")) continue;
                    var pars = mi.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(IntVec3) && mi.ReturnType == typeof(void))
                        return mi;
                }
                bt = bt.BaseType;
            }
            return null;
        }

        static bool Prepare() => TargetMethod() != null;

        public static bool Prefix(Designator __instance)
        {
            return Utilities.HandleDesignatePrefix(__instance);
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

                bool anyActive = settings.buildUseOverrideRotation || Utilities.MouseRotateEnabledFor(__instance, settings);
                if (!anyActive) return;

                // Apply build override once; keep allowing mouse rotate after
                Utilities.ApplyBuildOverrideIfNeeded(__instance, settings);
                // Sims-like mouse rotation is centralized in Utils.HandleMouseRotate
                if (Utilities.HandleMouseRotate(__instance)) return;
            }
            catch (Exception e)
            {
                Log.Warning("[PerfectPlacement] Place.SelectedUpdate failed: " + e.Message);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_Designator_Build_DesignateSingleCell_MouseRotate
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = typeof(Designator_Build);
            var m = AccessTools.Method(t, "DesignateSingleCell", new[] { typeof(IntVec3) });
            if (m != null) return m;
            foreach (var mi in AccessTools.GetDeclaredMethods(t))
            {
                if (!mi.Name.Contains("Designate")) continue;
                var pars = mi.GetParameters();
                if (pars.Length == 1 && pars[0].ParameterType == typeof(IntVec3) && mi.ReturnType == typeof(void))
                    return mi;
            }
            var bt = t.BaseType;
            while (bt != null)
            {
                m = AccessTools.Method(bt, "DesignateSingleCell", new[] { typeof(IntVec3) });
                if (m != null) return m;
                foreach (var mi in AccessTools.GetDeclaredMethods(bt))
                {
                    if (!mi.Name.Contains("Designate")) continue;
                    var pars = mi.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(IntVec3) && mi.ReturnType == typeof(void))
                        return mi;
                }
                bt = bt.BaseType;
            }
            return null;
        }

        static bool Prepare() => TargetMethod() != null;

        public static bool Prefix(Designator __instance)
        {
            return Utilities.HandleDesignatePrefix(__instance);
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

                bool anyActive = settings.buildUseOverrideRotation || Utilities.MouseRotateEnabledFor(__instance, settings);
                if (!anyActive) return;

                // Apply build override once; keep allowing mouse rotate after
                Utilities.ApplyBuildOverrideIfNeeded(__instance, settings);
                // Sims-like mouse rotation is centralized in Utils.HandleMouseRotate
                if (Utilities.HandleMouseRotate(__instance)) return;
            }
            catch (Exception e)
            {
                Log.Warning("[PerfectPlacement] Build.SelectedUpdate failed: " + e.Message);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_Designator_Place_ProcessInput_MouseRotate
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = typeof(Designator_Place);
            // Prefer exact signature: void ProcessInput(Event)
            var m = AccessTools.Method(t, "ProcessInput", new[] { typeof(Event) });
            if (m != null) return m;
            foreach (var mi in AccessTools.GetDeclaredMethods(t))
            {
                if (mi.Name != "ProcessInput") continue;
                var pars = mi.GetParameters();
                if (pars.Length == 1 && pars[0].ParameterType == typeof(Event) && mi.ReturnType == typeof(void))
                    return mi;
            }
            var bt = t.BaseType;
            while (bt != null)
            {
                m = AccessTools.Method(bt, "ProcessInput", new[] { typeof(Event) });
                if (m != null) return m;
                foreach (var mi in AccessTools.GetDeclaredMethods(bt))
                {
                    if (mi.Name != "ProcessInput") continue;
                    var pars = mi.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(Event) && mi.ReturnType == typeof(void))
                        return mi;
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

    [HarmonyPatch]
    public static class Patch_Designator_Build_ProcessInput_MouseRotate
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = typeof(Designator_Build);
            var m = AccessTools.Method(t, "ProcessInput", new[] { typeof(Event) });
            if (m != null) return m;
            foreach (var mi in AccessTools.GetDeclaredMethods(t))
            {
                if (mi.Name != "ProcessInput") continue;
                var pars = mi.GetParameters();
                if (pars.Length == 1 && pars[0].ParameterType == typeof(Event) && mi.ReturnType == typeof(void))
                    return mi;
            }
            var bt = t.BaseType;
            while (bt != null)
            {
                m = AccessTools.Method(bt, "ProcessInput", new[] { typeof(Event) });
                if (m != null) return m;
                foreach (var mi in AccessTools.GetDeclaredMethods(bt))
                {
                    if (mi.Name != "ProcessInput") continue;
                    var pars = mi.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(Event) && mi.ReturnType == typeof(void))
                        return mi;
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

    [HarmonyPatch]
    public static class Patch_Designator_Install_ProcessInput_MouseRotate
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = typeof(Designator_Install);
            var m = AccessTools.Method(t, "ProcessInput", new[] { typeof(Event) });
            if (m != null) return m;
            foreach (var mi in AccessTools.GetDeclaredMethods(t))
            {
                if (mi.Name != "ProcessInput") continue;
                var pars = mi.GetParameters();
                if (pars.Length == 1 && pars[0].ParameterType == typeof(Event) && mi.ReturnType == typeof(void))
                    return mi;
            }
            var bt = t.BaseType;
            while (bt != null)
            {
                m = AccessTools.Method(bt, "ProcessInput", new[] { typeof(Event) });
                if (m != null) return m;
                foreach (var mi in AccessTools.GetDeclaredMethods(bt))
                {
                    if (mi.Name != "ProcessInput") continue;
                    var pars = mi.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(Event) && mi.ReturnType == typeof(void))
                        return mi;
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

    // Removed: sound suppression patch

    [HarmonyPatch]
    public static class Patch_Designator_ProcessInput_MouseRotate
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = typeof(Designator);
            var m = AccessTools.Method(t, "ProcessInput", new[] { typeof(Event) });
            if (m != null) return m;
            foreach (var mi in AccessTools.GetDeclaredMethods(t))
            {
                if (mi.Name != "ProcessInput") continue;
                var pars = mi.GetParameters();
                if (pars.Length == 1 && pars[0].ParameterType == typeof(Event) && mi.ReturnType == typeof(void))
                    return mi;
            }
            var bt = t.BaseType;
            while (bt != null)
            {
                m = AccessTools.Method(bt, "ProcessInput", new[] { typeof(Event) });
                if (m != null) return m;
                foreach (var mi in AccessTools.GetDeclaredMethods(bt))
                {
                    if (mi.Name != "ProcessInput") continue;
                    var pars = mi.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(Event) && mi.ReturnType == typeof(void))
                        return mi;
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
                // Fast early-out: if nothing is pinned, run original
                if (!Utilities.HasAnyPinned) return true;
                var des = Utilities.CurrentSelectedDesignator();
                if (des == null) return true;

                var settings = PerfectPlacement.Settings;
                if (settings == null) return true;

                if (Utilities.MouseRotateEnabledFor(des, settings) && Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    Event.current.Use();
                    return false; // Skip original method
                }
            }
            catch { }
            return true; // Run original method
        }
    }

    // Suppress the "SpaceAlreadyOccupied" reject message globally via Messages.Message overloads
    [HarmonyPatch]
    public static class Patch_Messages_Message_Suppress_SpaceAlreadyOccupied_Tagged_WithHistorical
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Messages), "Message", new[] { typeof(TaggedString), typeof(MessageTypeDef), typeof(bool) });
        }
        static bool Prepare() => TargetMethod() != null;
        public static bool Prefix(TaggedString text, MessageTypeDef def, bool historical)
        {
            try { if (Utilities.ShouldSuppressDesignatorReject(text, def)) return false; } catch { }
            return true;
        }
    }

    [HarmonyPatch]
    public static class Patch_Messages_Message_Suppress_SpaceAlreadyOccupied_String_WithHistorical
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Messages), "Message", new[] { typeof(string), typeof(MessageTypeDef), typeof(bool) });
        }
        static bool Prepare() => TargetMethod() != null;
        public static bool Prefix(string text, MessageTypeDef def, bool historical)
        {
            try { if (Utilities.ShouldSuppressDesignatorReject(text, def)) return false; } catch { }
            return true;
        }
    }

    [HarmonyPatch]
    public static class Patch_Messages_Message_Suppress_SpaceAlreadyOccupied_Tagged
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Messages), "Message", new[] { typeof(TaggedString), typeof(MessageTypeDef) });
        }
        static bool Prepare() => TargetMethod() != null;
        public static bool Prefix(TaggedString text, MessageTypeDef def)
        {
            try { if (Utilities.ShouldSuppressDesignatorReject(text, def)) return false; } catch { }
            return true;
        }
    }

    [HarmonyPatch]
    public static class Patch_Messages_Message_Suppress_SpaceAlreadyOccupied_String
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Messages), "Message", new[] { typeof(string), typeof(MessageTypeDef) });
        }
        static bool Prepare() => TargetMethod() != null;
        public static bool Prefix(string text, MessageTypeDef def)
        {
            try { if (Utilities.ShouldSuppressDesignatorReject(text, def)) return false; } catch { }
            return true;
        }
    }

    [HarmonyPatch]
    public static class Patch_Messages_Message_Suppress_SpaceAlreadyOccupied_Tagged_WithTargets_WithHistorical
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Messages), "Message", new[] { typeof(TaggedString), typeof(LookTargets), typeof(MessageTypeDef), typeof(bool) });
        }
        static bool Prepare() => TargetMethod() != null;
        public static bool Prefix(TaggedString text, LookTargets lookTargets, MessageTypeDef def, bool historical)
        {
            try { if (Utilities.ShouldSuppressDesignatorReject(text, def)) return false; } catch { }
            return true;
        }
    }

    [HarmonyPatch]
    public static class Patch_Messages_Message_Suppress_SpaceAlreadyOccupied_String_WithTargets_WithHistorical
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Messages), "Message", new[] { typeof(string), typeof(LookTargets), typeof(MessageTypeDef), typeof(bool) });
        }
        static bool Prepare() => TargetMethod() != null;
        public static bool Prefix(string text, LookTargets lookTargets, MessageTypeDef def, bool historical)
        {
            try { if (Utilities.ShouldSuppressDesignatorReject(text, def)) return false; } catch { }
            return true;
        }
    }

    [HarmonyPatch]
    public static class Patch_Messages_Message_Suppress_SpaceAlreadyOccupied_Tagged_WithTargets
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Messages), "Message", new[] { typeof(TaggedString), typeof(LookTargets), typeof(MessageTypeDef) });
        }
        static bool Prepare() => TargetMethod() != null;
        public static bool Prefix(TaggedString text, LookTargets lookTargets, MessageTypeDef def)
        {
            try { if (Utilities.ShouldSuppressDesignatorReject(text, def)) return false; } catch { }
            return true;
        }
    }

    [HarmonyPatch]
    public static class Patch_Messages_Message_Suppress_SpaceAlreadyOccupied_String_WithTargets
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Messages), "Message", new[] { typeof(string), typeof(LookTargets), typeof(MessageTypeDef) });
        }
        static bool Prepare() => TargetMethod() != null;
        public static bool Prefix(string text, LookTargets lookTargets, MessageTypeDef def)
        {
            try { if (Utilities.ShouldSuppressDesignatorReject(text, def)) return false; } catch { }
            return true;
        }
    }
}
