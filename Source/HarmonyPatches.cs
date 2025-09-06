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
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;

            _isCached = true;
            var t = typeof(Designator_Install);
            var m = AccessTools.Method(t, "DesignateSingleCell", new[] { typeof(IntVec3) });
            if (m != null)
            {
                _cachedTarget = m;
                return _cachedTarget;
            }

            foreach (var mi in AccessTools.GetDeclaredMethods(t))
            {
                if (!mi.Name.Contains("Designate")) continue;
                var pars = mi.GetParameters();
                if (pars.Length == 1 && pars[0].ParameterType == typeof(IntVec3) && mi.ReturnType == typeof(void))
                {
                    _cachedTarget = mi;
                    return _cachedTarget;
                }
            }

            var bt = t.BaseType;
            while (bt != null)
            {
                m = AccessTools.Method(bt, "DesignateSingleCell", new[] { typeof(IntVec3) });
                if (m != null)
                {
                    _cachedTarget = m;
                    return _cachedTarget;
                }
                foreach (var mi in AccessTools.GetDeclaredMethods(bt))
                {
                    if (!mi.Name.Contains("Designate")) continue;
                    var pars = mi.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(IntVec3) && mi.ReturnType == typeof(void))
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
                    : settings.installUseOverrideRotation;
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

    [HarmonyPatch]
    public static class Patch_Designator_Place_DesignateSingleCell_MouseRotate
    {
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;

            _isCached = true;
            var t = typeof(Designator_Place);
            var m = AccessTools.Method(t, "DesignateSingleCell", new[] { typeof(IntVec3) });
            if (m != null)
            {
                _cachedTarget = m;
                return _cachedTarget;
            }
            foreach (var mi in AccessTools.GetDeclaredMethods(t))
            {
                if (!mi.Name.Contains("Designate")) continue;
                var pars = mi.GetParameters();
                if (pars.Length == 1 && pars[0].ParameterType == typeof(IntVec3) && mi.ReturnType == typeof(void))
                {
                    _cachedTarget = mi;
                    return _cachedTarget;
                }
            }
            var bt = t.BaseType;
            while (bt != null)
            {
                m = AccessTools.Method(bt, "DesignateSingleCell", new[] { typeof(IntVec3) });
                if (m != null)
                {
                    _cachedTarget = m;
                    return _cachedTarget;
                }
                foreach (var mi in AccessTools.GetDeclaredMethods(bt))
                {
                    if (!mi.Name.Contains("Designate")) continue;
                    var pars = mi.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(IntVec3) && mi.ReturnType == typeof(void))
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

                Utilities.ApplyBuildOverrideIfNeeded(__instance, settings);
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
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;

            _isCached = true;
            var t = typeof(Designator_Build);
            var m = AccessTools.Method(t, "DesignateSingleCell", new[] { typeof(IntVec3) });
            if (m != null)
            {
                _cachedTarget = m;
                return _cachedTarget;
            }
            foreach (var mi in AccessTools.GetDeclaredMethods(t))
            {
                if (!mi.Name.Contains("Designate")) continue;
                var pars = mi.GetParameters();
                if (pars.Length == 1 && pars[0].ParameterType == typeof(IntVec3) && mi.ReturnType == typeof(void))
                {
                    _cachedTarget = mi;
                    return _cachedTarget;
                }
            }
            var bt = t.BaseType;
            while (bt != null)
            {
                m = AccessTools.Method(bt, "DesignateSingleCell", new[] { typeof(IntVec3) });
                if (m != null)
                {
                    _cachedTarget = m;
                    return _cachedTarget;
                }
                foreach (var mi in AccessTools.GetDeclaredMethods(bt))
                {
                    if (!mi.Name.Contains("Designate")) continue;
                    var pars = mi.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(IntVec3) && mi.ReturnType == typeof(void))
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
    public static class Patch_Designator_Place_ProcessInput_MouseRotate
    {
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;

            _isCached = true;
            var t = typeof(Designator_Place);
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

    [HarmonyPatch]
    public static class Patch_Designator_Build_ProcessInput_MouseRotate
    {
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;

            _isCached = true;
            var t = typeof(Designator_Build);
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

    [HarmonyPatch]
    public static class Patch_Designator_Install_ProcessInput_MouseRotate
    {
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;

            _isCached = true;
            var t = typeof(Designator_Install);
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

    [HarmonyPatch]
    public static class Patch_Messages_Message_Suppress_SpaceAlreadyOccupied_Tagged_WithHistorical
    {
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;
            _isCached = true;
            _cachedTarget = AccessTools.Method(typeof(Messages), "Message", new[] { typeof(TaggedString), typeof(MessageTypeDef), typeof(bool) });
            return _cachedTarget;
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
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;
            _isCached = true;
            _cachedTarget = AccessTools.Method(typeof(Messages), "Message", new[] { typeof(string), typeof(MessageTypeDef), typeof(bool) });
            return _cachedTarget;
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
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;
            _isCached = true;
            _cachedTarget = AccessTools.Method(typeof(Messages), "Message", new[] { typeof(TaggedString), typeof(MessageTypeDef) });
            return _cachedTarget;
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
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;
            _isCached = true;
            _cachedTarget = AccessTools.Method(typeof(Messages), "Message", new[] { typeof(string), typeof(MessageTypeDef) });
            return _cachedTarget;
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
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;
            _isCached = true;
            _cachedTarget = AccessTools.Method(typeof(Messages), "Message", new[] { typeof(TaggedString), typeof(LookTargets), typeof(MessageTypeDef), typeof(bool) });
            return _cachedTarget;
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
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;
            _isCached = true;
            _cachedTarget = AccessTools.Method(typeof(Messages), "Message", new[] { typeof(string), typeof(LookTargets), typeof(MessageTypeDef), typeof(bool) });
            return _cachedTarget;
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
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;
            _isCached = true;
            _cachedTarget = AccessTools.Method(typeof(Messages), "Message", new[] { typeof(TaggedString), typeof(LookTargets), typeof(MessageTypeDef) });
            return _cachedTarget;
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
        private static MethodBase _cachedTarget;
        private static bool _isCached;

        static MethodBase TargetMethod()
        {
            if (_isCached) return _cachedTarget;
            _isCached = true;
            _cachedTarget = AccessTools.Method(typeof(Messages), "Message", new[] { typeof(string), typeof(LookTargets), typeof(MessageTypeDef) });
            return _cachedTarget;
        }
        static bool Prepare() => TargetMethod() != null;
        public static bool Prefix(string text, LookTargets lookTargets, MessageTypeDef def)
        {
            try { if (Utilities.ShouldSuppressDesignatorReject(text, def)) return false; } catch { }
            return true;
        }
    }
}