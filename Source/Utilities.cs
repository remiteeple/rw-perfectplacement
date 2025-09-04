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
    public static class Utilities
    {
        private const int MouseDeadzoneCells = 1; // small deadzone around pinned origin
        private static readonly HashSet<Designator> AppliedOnce = new HashSet<Designator>();
        private static readonly Dictionary<Designator, IntVec3> PinnedCell = new Dictionary<Designator, IntVec3>();
        private static readonly HashSet<Designator> PlacementArmed = new HashSet<Designator>();

        // Cache for expensive reflection lookups
        private static readonly Dictionary<Type, Action<Designator, Rot4>> SetRotCache = new Dictionary<Type, Action<Designator, Rot4>>();
        private static readonly Dictionary<Type, Func<Designator, Rot4>> GetRotCache = new Dictionary<Type, Func<Designator, Rot4>>();
        private static readonly object SetRotCacheLock = new object();
        private static readonly object GetRotCacheLock = new object();
        private static readonly Dictionary<Type, Func<Designator, Thing>> InstallSourceCache = new Dictionary<Type, Func<Designator, Thing>>();
        private static readonly object InstallSourceCacheLock = new object();
        private static readonly Dictionary<Type, Func<Designator, BuildableDef>> PlacingDefCache = new Dictionary<Type, Func<Designator, BuildableDef>>();
        private static readonly object PlacingDefCacheLock = new object();
        [ThreadStatic]
        private static bool _allowSuccessSoundOnce;
        private static readonly Dictionary<Type, Func<Designator, SoundDef>> SuccessSoundGetterCache = new Dictionary<Type, Func<Designator, SoundDef>>();
        private static readonly object SuccessSoundGetterCacheLock = new object();
        private static Func<Designator> _selectedGetter;
        private static bool _selectedGetterResolved;

        [ThreadStatic]
        private static bool _suppressMouseCellPin;

        [ThreadStatic]
        private static bool _suppressMouseAttachmentOnce;

        public static bool SuppressMouseCellPin => _suppressMouseCellPin;
        public static void SuppressNextMouseAttachment()
        {
            _suppressMouseAttachmentOnce = true;
        }
        public static bool TryConsumeSuppressNextMouseAttachment()
        {
            if (_suppressMouseAttachmentOnce)
            {
                _suppressMouseAttachmentOnce = false;
                return true;
            }
            return false;
        }

        private static readonly Dictionary<Designator, IntVec3> LastMouseCell = new Dictionary<Designator, IntVec3>();
        private static readonly HashSet<Designator> KeyboardOverrideUntilMove = new HashSet<Designator>();

        public static IntVec3 GetActualMouseCell()
        {
            bool prev = _suppressMouseCellPin;
            _suppressMouseCellPin = true;
            try
            {
                return UI.MouseCell();
            }
            finally
            {
                _suppressMouseCellPin = prev;
            }
        }

        public static bool MouseRotateEnabledFor(Designator d, PerfectPlacementSettings s)
        {
            if (d == null || s == null) return false;

            if (!s.globalMouseRotate) return false;

            // Only allow mouse rotation for rotatable targets
            return IsRotatable(d);
        }

        public static bool IsRotatable(Designator d)
        {
            try
            {
                if (d == null) return false;
                // Install/Reinstall designator: inspect the actual thing being installed
                if (d is Designator_Install)
                {
                    var src = FindSourceThingForInstall(d);
                    if (src != null)
                    {
                        var inner = TryGetInnerThing(src) ?? src;
                        var tdef = inner?.def as ThingDef;
                        if (tdef != null) return tdef.rotatable;
                    }
                    // Fallback to placing def if any
                    var pd = FindPlacingDef(d) as ThingDef;
                    return pd != null && pd.rotatable;
                }
                // Build/Place designators: check the placing BuildableDef
                if (d is Designator_Build || d is Designator_Place)
                {
                    var pd = FindPlacingDef(d) as ThingDef;
                    return pd != null && pd.rotatable;
                }
            }
            catch { }
            return false;
        }

        public static BuildableDef FindPlacingDef(Designator d)
        {
            if (d == null) return null;
            var t = d.GetType();
            Func<Designator, BuildableDef> getter;
            lock (PlacingDefCacheLock)
            {
                if (!PlacingDefCache.TryGetValue(t, out getter))
                {
                    getter = BuildPlacingDefGetter(t) ?? (_ => null);
                    PlacingDefCache[t] = getter;
                }
            }
            try { return getter(d); } catch { return null; }
        }

        private static Func<Designator, BuildableDef> BuildPlacingDefGetter(Type t)
        {
            try
            {
                // Prefer common property names first
                var propCandidates = new[] { "PlacingDef", "placingDef", "EntDef", "entDef", "BuildableDef", "buildableDef" };
                foreach (var name in propCandidates)
                {
                    var p = AccessTools.Property(t, name);
                    if (p != null && p.CanRead && typeof(BuildableDef).IsAssignableFrom(p.PropertyType))
                        return des => p.GetValue(des, null) as BuildableDef;
                }
                // Then common field names
                var fieldCandidates = new[] { "placingDef", "entDef", "buildableDef", "defToPlace" };
                foreach (var name in fieldCandidates)
                {
                    var f = AccessTools.Field(t, name);
                    if (f != null && typeof(BuildableDef).IsAssignableFrom(f.FieldType))
                        return des => f.GetValue(des) as BuildableDef;
                }
                // Fallback: any readable property/field of type BuildableDef on this type hierarchy
                var tCur = t;
                while (tCur != null)
                {
                    foreach (var p in tCur.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (p.CanRead && typeof(BuildableDef).IsAssignableFrom(p.PropertyType))
                            return des => p.GetValue(des, null) as BuildableDef;
                    }
                    foreach (var f in tCur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (typeof(BuildableDef).IsAssignableFrom(f.FieldType))
                            return des => f.GetValue(des) as BuildableDef;
                    }
                    tCur = tCur.BaseType;
                }
            }
            catch { }
            return null;
        }

        public static bool WasApplied(Designator d) => d != null && AppliedOnce.Contains(d);
        public static void MarkApplied(Designator d)
        {
            if (d == null) return;
            AppliedOnce.Add(d);
        }
        public static void UnmarkApplied(Designator d)
        {
            if (d == null) return;
            AppliedOnce.Remove(d);
        }

        public static bool TryGetPinned(Designator d, out IntVec3 cell)
        {
            if (d != null && PinnedCell.TryGetValue(d, out cell)) return true;
            cell = default;
            return false;
        }
        public static void SetPinned(Designator d, IntVec3 cell)
        {
            if (d == null) return;
            PinnedCell[d] = cell;
        }
        public static void ClearPinned(Designator d)
        {
            if (d == null) return;
            PinnedCell.Remove(d);
        }

        public static void ArmPlacement(Designator d)
        {
            if (d == null) return;
            PlacementArmed.Add(d);
        }
        public static bool TryConsumePlacementArmed(Designator d)
        {
            if (d == null) return false;
            if (PlacementArmed.Contains(d))
            {
                PlacementArmed.Remove(d);
                return true;
            }
            return false;
        }

        public static Designator CurrentSelectedDesignator()
        {
            try
            {
                if (!_selectedGetterResolved)
                {
                    _selectedGetterResolved = true;
                    _selectedGetter = BuildSelectedGetter();
                }
                if (_selectedGetter != null)
                    return _selectedGetter();
                var dm = Find.DesignatorManager;
                if (dm == null) return null;
                var t = dm.GetType();
                var p = AccessTools.Property(t, "Selected") ?? AccessTools.Property(t, "SelectedDesignator");
                if (p != null && p.CanRead) return p.GetValue(dm, null) as Designator;
                var f = AccessTools.Field(t, "selected") ?? AccessTools.Field(t, "selectedDesignator");
                if (f != null) return f.GetValue(dm) as Designator;
            }
            catch { }
            return null;
        }

        private static Func<Designator> BuildSelectedGetter()
        {
            try
            {
                var tMan = typeof(DesignatorManager);
                var p = AccessTools.Property(tMan, "Selected") ?? AccessTools.Property(tMan, "SelectedDesignator");
                if (p != null && p.CanRead)
                {
                    return () =>
                    {
                        var dm = Find.DesignatorManager;
                        return dm != null ? (p.GetValue(dm, null) as Designator) : null;
                    };
                }
                var f = AccessTools.Field(tMan, "selected") ?? AccessTools.Field(tMan, "selectedDesignator");
                if (f != null)
                {
                    return () =>
                    {
                        var dm = Find.DesignatorManager;
                        return dm != null ? (f.GetValue(dm) as Designator) : null;
                    };
                }
            }
            catch { }
            return null;
        }

        public static bool SetAllPlacingRotFields(Designator d, Rot4 value)
        {
            if (d == null) return false;
            var type = d.GetType();
            Action<Designator, Rot4> setter;
            lock (SetRotCacheLock)
            {
                if (!SetRotCache.TryGetValue(type, out setter))
                {
                    setter = BuildPlacingRotSetter(type);
                    SetRotCache[type] = setter; // may be null if not found
                }
            }
            if (setter != null)
            {
                setter(d, value);
                return true;
            }
            return false;
        }

        private static Action<Designator, Rot4> BuildPlacingRotSetter(Type type)
        {
            try
            {
                var t = type;
                while (t != null)
                {
                    var f = AccessTools.Field(t, "placingRot");
                    if (f != null && f.FieldType == typeof(Rot4))
                    {
                        return (des, rot) => f.SetValue(des, rot);
                    }
                    t = t.BaseType;
                }
                t = type;
                while (t != null)
                {
                    var p = AccessTools.Property(t, "placingRot") ?? AccessTools.Property(t, "PlacingRot");
                    if (p != null && p.CanWrite && p.PropertyType == typeof(Rot4))
                    {
                        return (des, rot) => p.SetValue(des, rot, null);
                    }
                    t = t.BaseType;
                }
            }
            catch { }
            return null;
        }

        public static bool TryGetPlacingRot(Designator d, out Rot4 value)
        {
            value = default;
            if (d == null) return false;
            var type = d.GetType();
            Func<Designator, Rot4> getter;
            lock (GetRotCacheLock)
            {
                if (!GetRotCache.TryGetValue(type, out getter))
                {
                    getter = BuildPlacingRotGetter(type);
                    GetRotCache[type] = getter; // may be null if not found
                }
            }
            if (getter != null)
            {
                try { value = getter(d); return true; } catch { return false; }
            }
            return false;
        }

        private static Func<Designator, Rot4> BuildPlacingRotGetter(Type type)
        {
            try
            {
                var t = type;
                while (t != null)
                {
                    var f = AccessTools.Field(t, "placingRot");
                    if (f != null && f.FieldType == typeof(Rot4))
                    {
                        return des => (Rot4)f.GetValue(des);
                    }
                    t = t.BaseType;
                }
                t = type;
                while (t != null)
                {
                    var p = AccessTools.Property(t, "placingRot") ?? AccessTools.Property(t, "PlacingRot");
                    if (p != null && p.CanRead && p.PropertyType == typeof(Rot4))
                    {
                        return des => (Rot4)p.GetValue(des, null);
                    }
                    t = t.BaseType;
                }
            }
            catch { }
            return null;
        }

        // True only when placing a building being reinstalled from the map (not a MinifiedThing)
        public static bool IsReinstallDesignator(Designator d, out Thing source)
        {
            source = FindSourceThingForInstall(d);
            if (source == null) return false;
            var inner = TryGetInnerThing(source);
            return inner == null;
        }

        public static Thing FindSourceThingForInstall(Designator d)
        {
            if (d == null) return null;
            var t = d.GetType();
            Func<Designator, Thing> getter;
            lock (InstallSourceCacheLock)
            {
                if (!InstallSourceCache.TryGetValue(t, out getter))
                {
                    getter = BuildInstallSourceGetter(t) ?? (_ => null);
                    InstallSourceCache[t] = getter;
                }
            }
            try { return getter(d); } catch { return null; }
        }

        private static Func<Designator, Thing> BuildInstallSourceGetter(Type t)
        {
            try
            {
                var propCandidates = new[] { "MiniToInstallOrBuildingToReinstall", "ThingToInstall" };
                foreach (var propName in propCandidates)
                {
                    var p = AccessTools.Property(t, propName);
                    if (p != null && p.CanRead && typeof(Thing).IsAssignableFrom(p.PropertyType))
                        return des => p.GetValue(des, null) as Thing;
                }

                var fieldCandidates = new[] { "thingToInstall", "ent", "installThing", "minifiedThing", "reinstall", "miniToInstallOrBuildingToReinstall" };
                foreach (var fieldName in fieldCandidates)
                {
                    var f = AccessTools.Field(t, fieldName);
                    if (f != null && typeof(Thing).IsAssignableFrom(f.FieldType))
                        return des => f.GetValue(des) as Thing;
                }

                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (typeof(Thing).IsAssignableFrom(f.FieldType))
                        return des => f.GetValue(des) as Thing;
                }
                foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (p.CanRead && typeof(Thing).IsAssignableFrom(p.PropertyType))
                        return des => p.GetValue(des, null) as Thing;
                }
            }
            catch { }
            return null;
        }

        public static Thing TryGetInnerThing(Thing thing)
        {
            if (thing == null) return null;
            try
            {
                var mt = thing.GetType();
                if (mt.Name == "MinifiedThing" || (mt.FullName?.Contains(".MinifiedThing") ?? false))
                {
                    var p = AccessTools.Property(mt, "InnerThing");
                    if (p != null && p.CanRead)
                    {
                        return p.GetValue(thing, null) as Thing;
                    }
                }
            }
            catch { }
            return null;
        }

        public static Rot4 DirectionFromDelta(int dx, int dz)
        {
            if (dx == 0 && dz == 0) return default;
            return Mathf.Abs(dx) >= Mathf.Abs(dz)
                ? (dx >= 0 ? Rot4.East : Rot4.West)
                : (dz >= 0 ? Rot4.North : Rot4.South);
        }

        public static bool HandleMouseRotate(Designator des)
        {
            var s = PerfectPlacement.Settings;
            if (s == null) return false;
            if (!MouseRotateEnabledFor(des, s)) return false;

            var evt = Event.current;
            bool mouseDownNow = (evt != null && evt.type == EventType.MouseDown && evt.button == 0) || Input.GetMouseButtonDown(0);
            if (mouseDownNow)
            {
                if (!TryGetPinned(des, out var _))
                {
                    var pin = GetActualMouseCell();
                    SetPinned(des, pin);
                    PlayPinSound(des);
                }
                TryGetPinned(des, out var pinCell);
                var cur = GetActualMouseCell();
                LastMouseCell[des] = cur;
                int dx = cur.x - pinCell.x;
                int dz = cur.z - pinCell.z;
                bool inDeadzone = Math.Abs(dx) <= MouseDeadzoneCells && Math.Abs(dz) <= MouseDeadzoneCells;
                if (!inDeadzone && (dx != 0 || dz != 0))
                {
                    var desired = DirectionFromDelta(dx, dz);
                    if (!TryGetPlacingRot(des, out var curRot) || curRot != desired)
                    {
                        SetAllPlacingRotFields(des, desired);
                        PlayRotateSound(des);
                    }
                }
                // Consume the click so vanilla doesn't attempt to place/select in GUI path
                if (evt != null && evt.type == EventType.MouseDown && evt.button == 0)
                {
                    try { evt.Use(); } catch { }
                }
            }

            if (Input.GetMouseButton(0) && TryGetPinned(des, out var pinned))
            {
                var cur = GetActualMouseCell();
                bool mouseMoved = !LastMouseCell.TryGetValue(des, out var last) || last != cur;
                LastMouseCell[des] = cur;
                if (mouseMoved) KeyboardOverrideUntilMove.Remove(des);

                int dx = cur.x - pinned.x;
                int dz = cur.z - pinned.z;
                bool inDeadzone = Math.Abs(dx) <= MouseDeadzoneCells && Math.Abs(dz) <= MouseDeadzoneCells;

                // Keyboard override: if not moving, allow Q/E to rotate instead of mouse
                try
                {
                    bool left = KeyBindingDefOf.Designator_RotateLeft.KeyDownEvent;
                    bool right = KeyBindingDefOf.Designator_RotateRight.KeyDownEvent;
                    if ((left || right) && !mouseMoved)
                    {
                        if (TryGetPlacingRot(des, out var curRot))
                        {
                            int delta = right ? 1 : (left ? -1 : 0);
                            if (delta != 0)
                            {
                                int idx = (curRot.AsInt + (delta + 4)) & 3;
                                var newRot = new Rot4(idx);
                                if (newRot != curRot)
                                {
                                    SetAllPlacingRotFields(des, newRot);
                                    PlayRotateSound(des);
                                }
                                KeyboardOverrideUntilMove.Add(des);
                            }
                        }
                    }
                }
                catch { }

                // Mouse rotation if outside deadzone and no active keyboard override
                if (!inDeadzone && (!KeyboardOverrideUntilMove.Contains(des) || mouseMoved))
                {
                    if (dx != 0 || dz != 0)
                    {
                        var desired = DirectionFromDelta(dx, dz);
                        if (!TryGetPlacingRot(des, out var curRot) || curRot != desired)
                        {
                            SetAllPlacingRotFields(des, desired);
                            PlayRotateSound(des);
                        }
                    }
                }
            }

            bool released = (evt != null && evt.type == EventType.MouseUp && evt.button == 0) || Input.GetMouseButtonUp(0);
            if (released && TryGetPinned(des, out var toPlace))
            {
                var report = des.CanDesignateCell(toPlace);
                if (report.Accepted)
                {
                    ArmPlacement(des);
                    des.DesignateSingleCell(toPlace);
                    // Vanilla success sound may not play in this deferred path; play it explicitly.
                    PlayDesignateSuccessSound(des);
                }
                else
                {
                    // Show the rejection message after release on invalid location
                    try
                    {
                        if (!string.IsNullOrEmpty(report.Reason))
                        {
                            // Message will show naturally since mouse is released
                            Messages.Message(report.Reason, MessageTypeDefOf.RejectInput, historical: false);
                        }
                    }
                    catch { }
                }
                ClearPinned(des);
                LastMouseCell.Remove(des);
                KeyboardOverrideUntilMove.Remove(des);
                if (evt != null && evt.type == EventType.MouseUp && evt.button == 0) evt.Use();
                return true;
            }
            return false;
        }

        public static bool HandleDesignatePrefix(Designator des)
        {
            var settings = PerfectPlacement.Settings;
            if (settings == null) return true;
            bool enabled = MouseRotateEnabledFor(des, settings);
            if (!enabled) return true;
            if (!TryGetPinned(des, out var _))
            {
                var pin = GetActualMouseCell();
                SetPinned(des, pin);
                // Ensure pin sound plays when pin originates from DesignateSingleCell path
                PlayPinSound(des);
                // Suppress any immediate mouse-attachment warnings on the pin frame
                SuppressNextMouseAttachment();
                return false;
            }
            if (TryGetPinned(des, out var _))
            {
                if (TryConsumePlacementArmed(des))
                    return true;
                return false;
            }
            return true;
        }

        public static bool ApplyOverrideOnce(Designator des, Rot4 desired)
        {
            if (des == null) return false;
            if (WasApplied(des)) return false;
            if (SetAllPlacingRotFields(des, desired))
            {
                MarkApplied(des);
                return true;
            }
            return false;
        }

        public static bool ApplyInstallOrReinstallOverrideIfNeeded(Designator des, PerfectPlacementSettings s, bool isReinstall)
        {
            if (des == null || s == null) return false;
            if (!IsRotatable(des)) return false;
            if (isReinstall)
            {
                if (!s.useOverrideRotation) return false;
                return ApplyOverrideOnce(des, s.overrideRotation);
            }
            else
            {
                if (!s.installUseOverrideRotation) return false;
                return ApplyOverrideOnce(des, s.installOverrideRotation);
            }
        }

        public static bool ApplyBuildOverrideIfNeeded(Designator des, PerfectPlacementSettings s)
        {
            if (des == null || s == null) return false;
            if (!s.buildUseOverrideRotation) return false;
            if (!IsRotatable(des)) return false;
            return ApplyOverrideOnce(des, s.buildOverrideRotation);
        }

        public static bool HandleProcessInputPrefix(Designator des, Event evt)
        {
            var s = PerfectPlacement.Settings;
            if (s == null) return true;
            if (!MouseRotateEnabledFor(des, s)) return true;
            if (evt == null) return true;
            if (evt.type == EventType.MouseDown && evt.button == 0)
            {
                if (!TryGetPinned(des, out var _))
                {
                    var pin = GetActualMouseCell();
                    SetPinned(des, pin);
                    PlayPinSound(des);
                    // Suppress any immediate mouse-attachment warnings on the pin frame
                    SuppressNextMouseAttachment();
                }
                try { evt.Use(); } catch { }
                return false; // swallow original to prevent early placement/sound
            }
            return true;
        }

        private static SoundDef ResolveSound(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return null;
            try { return DefDatabase<SoundDef>.GetNamedSilentFail(defName); } catch { return null; }
        }

        public static void AllowNextSuccessSound()
        {
            _allowSuccessSoundOnce = true;
        }

        private static Func<Designator, SoundDef> BuildSuccessSoundGetter(Type t)
        {
            try
            {
                // Prefer exact field/property name used by Designator
                var tCur = t;
                while (tCur != null)
                {
                    var f = AccessTools.Field(tCur, "soundSucceeded");
                    if (f != null && typeof(SoundDef).IsAssignableFrom(f.FieldType))
                        return des => f.GetValue(des) as SoundDef;
                    tCur = tCur.BaseType;
                }
                tCur = t;
                while (tCur != null)
                {
                    var p = AccessTools.Property(tCur, "soundSucceeded");
                    if (p != null && p.CanRead && typeof(SoundDef).IsAssignableFrom(p.PropertyType))
                        return des => p.GetValue(des, null) as SoundDef;
                    tCur = tCur.BaseType;
                }
            }
            catch { }
            return null;
        }

        private static SoundDef TryGetDesignatorSuccessSound(Designator des)
        {
            if (des == null) return null;
            var t = des.GetType();
            Func<Designator, SoundDef> getter;
            lock (SuccessSoundGetterCacheLock)
            {
                if (!SuccessSoundGetterCache.TryGetValue(t, out getter))
                {
                    getter = BuildSuccessSoundGetter(t);
                    SuccessSoundGetterCache[t] = getter; // may be null
                }
            }
            try { return getter != null ? getter(des) : null; } catch { return null; }
        }

        public static void PlayDesignateSuccessSound(Designator des)
        {
            try
            {
                SoundDef sd = TryGetDesignatorSuccessSound(des);
                if (sd == null)
                {
                    // Fallback to vanilla placement sound DefName
                    sd = ResolveSound("Designate_Place");
                }
                if (sd != null)
                {
                    // Mark the next sound as allowed so our global suppression won't swallow it
                    AllowNextSuccessSound();
                    var map = des?.Map ?? Find.CurrentMap;
                    SoundStarter.PlayOneShotOnCamera(sd, map);
                }
            }
            catch { }
        }

        public static bool ShouldSuppressPlacementSound(SoundDef sd)
        {
            try
            {
                if (sd == null) return false;
                if (_allowSuccessSoundOnce)
                {
                    // Consume the allowance and permit this one
                    _allowSuccessSoundOnce = false;
                    return false;
                }
                var s = PerfectPlacement.Settings;
                if (s == null) return false;
                var des = CurrentSelectedDesignator();
                if (des == null) return false;
                if (!MouseRotateEnabledFor(des, s)) return false;
                if (!TryGetPinned(des, out var _)) return false;
                // Suppress if vanilla tries to play the designator's success sound or the generic place sound
                var succ = TryGetDesignatorSuccessSound(des);
                if (succ != null && ReferenceEquals(sd, succ)) return true;
                if (sd.defName == "Designate_Place") return true;
                // Also suppress generic UI click that may fire on MouseDown when pinning
                if (sd.defName == "Click") return true;
            }
            catch { }
            return false;
        }

        public static void PlayPinSound(Designator des)
        {
            try
            {
                var sd = ResolveSound("Designate_DragBuilding_Start");
                if (sd != null)
                {
                    // Ensure our pin sound isn't swallowed by suppression
                    AllowNextSuccessSound();
                    var map = des?.Map ?? Find.CurrentMap;
                    SoundStarter.PlayOneShotOnCamera(sd, map);
                }
            }
            catch { }
        }

        public static void PlayRotateSound(Designator des)
        {
            try
            {
                // Ensure rotate sound isn't swallowed if suppression becomes broader
                AllowNextSuccessSound();
                // Use static UI drag slider sound for rotation feedback
                SoundDefOf.DragSlider.PlayOneShotOnCamera();
            }
            catch { }
        }

        public static void ConsumeLeftClickIfMouseRotate(Designator des)
        {
            var s = PerfectPlacement.Settings;
            if (s == null) return;
            if (!MouseRotateEnabledFor(des, s)) return;
            var evt = Event.current;
            bool mouseDownNow = (evt != null && evt.type == EventType.MouseDown && evt.button == 0) || Input.GetMouseButtonDown(0);
            if (mouseDownNow)
            {
                if (!TryGetPinned(des, out var _))
                {
                    var pin = GetActualMouseCell();
                    SetPinned(des, pin);
                    PlayPinSound(des);
                    // Suppress any immediate mouse-attachment warnings on the pin frame
                    SuppressNextMouseAttachment();
                }
                if (evt != null && evt.type == EventType.MouseDown && evt.button == 0)
                {
                    try { evt.Use(); } catch { }
                }
            }
        }
    }
}