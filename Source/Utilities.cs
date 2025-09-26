using System;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using System.Linq.Expressions;
using RimWorld;
using Verse;
using UnityEngine;
using Verse.Sound;
using System.Runtime.CompilerServices;

namespace PerfectPlacement
{
    public static class Utilities
    {
        private const int MouseDeadzoneCells = 1; // small deadzone around pinned origin
        private static readonly ConditionalWeakTable<Designator, object> AppliedOnce = new ConditionalWeakTable<Designator, object>();
        private static readonly Dictionary<Designator, IntVec3> PinnedCell = new Dictionary<Designator, IntVec3>();
        private static readonly Dictionary<Designator, int> PinSessionId = new Dictionary<Designator, int>();
        private static int NextPinSessionId = 1;
        // Cache for expensive reflection lookups
        private static readonly Dictionary<Type, Action<Designator, Rot4>> SetRotCache = new Dictionary<Type, Action<Designator, Rot4>>();
        private static readonly Dictionary<Type, Func<Designator, Rot4>> GetRotCache = new Dictionary<Type, Func<Designator, Rot4>>();
        private static readonly Dictionary<Type, Func<Designator, Thing>> InstallSourceCache = new Dictionary<Type, Func<Designator, Thing>>();
        private static readonly Dictionary<Type, Func<Designator, BuildableDef>> PlacingDefCache = new Dictionary<Type, Func<Designator, BuildableDef>>();
        private static readonly Dictionary<Type, Func<Designator, SoundDef>> SuccessSoundGetterCache = new Dictionary<Type, Func<Designator, SoundDef>>();
        private static readonly Dictionary<string, SoundDef> SoundCache = new Dictionary<string, SoundDef>();
        private static Func<Designator> _selectedGetter;
        private static bool _selectedGetterResolved;
        private static readonly Dictionary<Type, Func<Designator>> SelectedGetterByMgrType = new Dictionary<Type, Func<Designator>>();
        private static readonly Dictionary<Type, Func<Thing, Thing>> InnerThingGetterCache = new Dictionary<Type, Func<Thing, Thing>>();

        private class CacheData
        {
            public bool? IsReinstall;
            public bool? IsRotatable;
        }
        private static ConditionalWeakTable<Designator, CacheData> InstanceCache = new ConditionalWeakTable<Designator, CacheData>();

        [ThreadStatic]
        private static bool _suppressMouseCellPin;

        private static readonly HashSet<string> DebugLoggedKeys = new HashSet<string>();

        public static bool DebugEnabled => PerfectPlacement.Settings?.debugLogs ?? false;
        public static void DebugLog(string msg)
        {
            try
            {
                if (!DebugEnabled) return;
                Log.Message("[PerfectPlacement/Debug] " + msg);
            }
            catch { }
        }
        public static void DebugLog(System.Func<string> messageFactory)
        {
            try
            {
                if (!DebugEnabled) return;
                if (messageFactory == null) return;
                Log.Message("[PerfectPlacement/Debug] " + messageFactory());
            }
            catch { }
        }
        public static void DebugLogOnceForCurrentPin(Designator des, string tag, Func<string> messageFactory)
        {
            try
            {
                if (!DebugEnabled || des == null || messageFactory == null) return;
                if (!TryGetPinSessionId(des, out var id)) return;
                string key = id.ToString() + ":" + (tag ?? "");
                if (DebugLoggedKeys.Add(key))
                {
                    DebugLog(messageFactory());
                }
            }
            catch { }
        }

        public static bool SuppressMouseCellPin => _suppressMouseCellPin;
        // Removed: Mouse attachment suppression; we no longer suppress in-preview overlays/messages

        private static readonly Dictionary<Designator, IntVec3> LastMouseCell = new Dictionary<Designator, IntVec3>();
        private static readonly Dictionary<Designator, bool> RotatableDesignatorCache = new Dictionary<Designator, bool>();
        private static readonly HashSet<Designator> KeyboardOverrideUntilMove = new HashSet<Designator>();

        public static bool HasAnyPinned => PinnedCell.Count != 0;

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

        public static bool TryGetPinSessionId(Designator d, out int id)
        {
            if (d != null && PinSessionId.TryGetValue(d, out id)) return true;
            id = 0;
            return false;
        }

        public static bool IsRotatable(Designator d)
        {
            if (d == null) return false;

            if (InstanceCache.TryGetValue(d, out var cache) && cache.IsRotatable.HasValue)
            {
                return cache.IsRotatable.Value;
            }

            try
            {
                if (RotatableDesignatorCache.TryGetValue(d, out var cached)) return cached;
                bool result = false;
                if (d is Designator_Install)
                {
                    var src = FindSourceThingForInstall(d);
                    if (src != null)
                    {
                        var inner = TryGetInnerThing(src) ?? src;
                        var tdef = inner?.def as ThingDef;
                        if (tdef != null) { result = tdef.rotatable; }
                    }
                    else
                    {
                        var pd = FindPlacingDef(d) as ThingDef;
                        result = pd != null && pd.rotatable;
                    }
                }
                else if (d is Designator_Build || d is Designator_Place)
                {
                    var pd = FindPlacingDef(d) as ThingDef;
                    result = pd != null && pd.rotatable;
                }

                if (InstanceCache.TryGetValue(d, out cache))
                {
                    cache.IsRotatable = result;
                }
                else
                {
                    InstanceCache.Add(d, new CacheData { IsRotatable = result });
                }

                RotatableDesignatorCache[d] = result;
                return result;
            }
            catch { }
            return false;
        }

        public static BuildableDef FindPlacingDef(Designator d)
        {
            if (d == null) return null;
            var t = d.GetType();
            Func<Designator, BuildableDef> getter;
            if (!PlacingDefCache.TryGetValue(t, out getter))
            {
                getter = BuildPlacingDefGetter(t) ?? (_ => null);
                PlacingDefCache[t] = getter;
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
                    {
                        var get = p.GetGetMethod(true);
                        if (get != null)
                        {
                            var param = System.Linq.Expressions.Expression.Parameter(typeof(Designator), "d");
                            var cast = System.Linq.Expressions.Expression.Convert(param, t);
                            var call = System.Linq.Expressions.Expression.Call(cast, get);
                            var asBuildable = System.Linq.Expressions.Expression.Convert(call, typeof(BuildableDef));
                            return System.Linq.Expressions.Expression.Lambda<Func<Designator, BuildableDef>>(asBuildable, param).Compile();
                        }
                    }
                }
                // Then common field names
                var fieldCandidates = new[] { "placingDef", "entDef", "buildableDef", "defToPlace" };
                foreach (var name in fieldCandidates)
                {
                    var f = AccessTools.Field(t, name);
                    if (f != null && typeof(BuildableDef).IsAssignableFrom(f.FieldType))
                    {
                        var param = System.Linq.Expressions.Expression.Parameter(typeof(Designator), "d");
                        var cast = System.Linq.Expressions.Expression.Convert(param, t);
                        var fld = System.Linq.Expressions.Expression.Field(cast, f);
                        var asBuildable = System.Linq.Expressions.Expression.Convert(fld, typeof(BuildableDef));
                        return System.Linq.Expressions.Expression.Lambda<Func<Designator, BuildableDef>>(asBuildable, param).Compile();
                    }
                }
                // Fallback: any readable property/field of type BuildableDef on this type hierarchy
                var tCur = t;
                while (tCur != null)
                {
                    foreach (var p in tCur.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (p.CanRead && typeof(BuildableDef).IsAssignableFrom(p.PropertyType))
                        {
                            var get = p.GetGetMethod(true);
                            if (get != null)
                            {
                                var param = System.Linq.Expressions.Expression.Parameter(typeof(Designator), "d");
                                var cast = System.Linq.Expressions.Expression.Convert(param, tCur);
                                var call = System.Linq.Expressions.Expression.Call(cast, get);
                                var asBuildable = System.Linq.Expressions.Expression.Convert(call, typeof(BuildableDef));
                                return System.Linq.Expressions.Expression.Lambda<Func<Designator, BuildableDef>>(asBuildable, param).Compile();
                            }
                        }
                    }
                    foreach (var f in tCur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (typeof(BuildableDef).IsAssignableFrom(f.FieldType))
                        {
                            var param = System.Linq.Expressions.Expression.Parameter(typeof(Designator), "d");
                            var cast = System.Linq.Expressions.Expression.Convert(param, tCur);
                            var fld = System.Linq.Expressions.Expression.Field(cast, f);
                            var asBuildable = System.Linq.Expressions.Expression.Convert(fld, typeof(BuildableDef));
                            return System.Linq.Expressions.Expression.Lambda<Func<Designator, BuildableDef>>(asBuildable, param).Compile();
                        }
                    }
                    tCur = tCur.BaseType;
                }
            }
            catch { }
            return null;
        }

        public static bool WasApplied(Designator d) => d != null && AppliedOnce.TryGetValue(d, out _);
        public static void MarkApplied(Designator d)
        {
            if (d == null) return;
            try { AppliedOnce.Add(d, new object()); } catch { }
        }
        public static void UnmarkApplied(Designator d)
        {
            if (d == null) return;
            try { AppliedOnce.Remove(d); } catch { }
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
            bool isNew = !PinnedCell.ContainsKey(d);
            PinnedCell[d] = cell;
            if (isNew)
            {
                PinSessionId[d] = NextPinSessionId++;
                DebugLog(() => $"Pinned start: des={d.GetType().Name}, cell={cell}");
            }
        }
        public static void ClearPinned(Designator d)
        {
            if (d == null) return;
            PinnedCell.Remove(d);
            if (PinSessionId.TryGetValue(d, out var id))
            {
                PinSessionId.Remove(d);
                DebugLog(() => $"Pinned end: des={d.GetType().Name}, session={id}");
                try
                {
                    // Clean up any once-per-session debug keys
                    var prefix = id.ToString() + ":";
                    DebugLoggedKeys.RemoveWhere(k => k != null && k.StartsWith(prefix));
                }
                catch { }
            }
        }

        public static void ClearRotatableCache()
        {
            RotatableDesignatorCache.Clear();
            // ConditionalWeakTable.Clear is not available on older frameworks (e.g., 1.4 toolchain).
            // Reinitialize the table to effectively clear cached entries across all target versions.
            InstanceCache = new ConditionalWeakTable<Designator, CacheData>();
        }

        public static void ClearTransientAll()
        {
            LastMouseCell.Clear();
            KeyboardOverrideUntilMove.Clear();
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
                if (!SelectedGetterByMgrType.TryGetValue(t, out var runtimeGetter))
                {
                    runtimeGetter = BuildSelectedGetterForManagerType(t);
                    SelectedGetterByMgrType[t] = runtimeGetter; // may be null; negative cache
                }
                if (runtimeGetter != null)
                {
                    _selectedGetter = runtimeGetter;
                    return _selectedGetter();
                }
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
                    var get = p.GetGetMethod(true);
                    if (get != null)
                    {
                        var del = AccessTools.MethodDelegate<Func<DesignatorManager, Designator>>(get);
                        return () =>
                        {
                            var dm = Find.DesignatorManager;
                            return dm != null ? del(dm) : null;
                        };
                    }
                }
                var f = AccessTools.Field(tMan, "selected") ?? AccessTools.Field(tMan, "selectedDesignator");
                if (f != null)
                {
                    // Compile a typed field getter: (DesignatorManager m) => (Designator) ((<tMan>)m).<field>
                    var param = Expression.Parameter(typeof(DesignatorManager), "m");
                    var cast = Expression.Convert(param, tMan);
                    var fieldAccess = Expression.Field(cast, f);
                    var asDesignator = Expression.Convert(fieldAccess, typeof(Designator));
                    var lambda = Expression.Lambda<Func<DesignatorManager, Designator>>(asDesignator, param).Compile();
                    return () =>
                    {
                        var dm = Find.DesignatorManager;
                        return dm != null ? lambda(dm) : null;
                    };
                }
            }
            catch { }
            return null;
        }

        private static Func<Designator> BuildSelectedGetterForManagerType(Type mgrType)
        {
            try
            {
                if (mgrType == null) return null;
                // Try property first
                var p = AccessTools.Property(mgrType, "Selected") ?? AccessTools.Property(mgrType, "SelectedDesignator");
                if (p != null && p.CanRead)
                {
                    var get = p.GetGetMethod(true);
                    if (get != null)
                    {
                        // Use object instance to be flexible across derived types
                        var del = AccessTools.MethodDelegate<Func<object, Designator>>(get);
                        return () =>
                        {
                            var dm = Find.DesignatorManager;
                            return dm != null ? del(dm) : null;
                        };
                    }
                }
                // Then try field(s)
                var f = AccessTools.Field(mgrType, "selected") ?? AccessTools.Field(mgrType, "selectedDesignator");
                if (f != null)
                {
                    // Compile a field getter against the specific runtime type
                    var paramObj = Expression.Parameter(typeof(object), "o");
                    var cast = Expression.Convert(paramObj, mgrType);
                    var fld = Expression.Field(cast, f);
                    var asDesignator = Expression.Convert(fld, typeof(Designator));
                    var compiled = Expression.Lambda<Func<object, Designator>>(asDesignator, paramObj).Compile();
                    return () =>
                    {
                        var dm = Find.DesignatorManager;
                        return dm != null ? compiled(dm) : null;
                    };
                }
                // Fallback: any readable property/field of type Designator on this manager type hierarchy
                var tCur = mgrType;
                while (tCur != null)
                {
                    foreach (var prop in tCur.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!prop.CanRead) continue;
                        if (!typeof(Designator).IsAssignableFrom(prop.PropertyType)) continue;
                        var get = prop.GetGetMethod(true);
                        if (get == null) continue;
                        var del = AccessTools.MethodDelegate<Func<object, Designator>>(get);
                        return () =>
                        {
                            var dm = Find.DesignatorManager;
                            return dm != null ? del(dm) : null;
                        };
                    }
                    foreach (var fldInfo in tCur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!typeof(Designator).IsAssignableFrom(fldInfo.FieldType)) continue;
                        var paramObj = Expression.Parameter(typeof(object), "o");
                        var castAny = Expression.Convert(paramObj, tCur);
                        var fld2 = Expression.Field(castAny, fldInfo);
                        var asDesignator2 = Expression.Convert(fld2, typeof(Designator));
                        var compiled2 = Expression.Lambda<Func<object, Designator>>(asDesignator2, paramObj).Compile();
                        return () =>
                        {
                            var dm = Find.DesignatorManager;
                            return dm != null ? compiled2(dm) : null;
                        };
                    }
                    tCur = tCur.BaseType;
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
            if (!SetRotCache.TryGetValue(type, out setter))
            {
                setter = BuildPlacingRotSetter(type);
                SetRotCache[type] = setter; // may be null if not found
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
                        var fieldRef = AccessTools.FieldRefAccess<Designator, Rot4>(f);
                        return (des, rot) => { fieldRef(des) = rot; };
                    }
                    t = t.BaseType;
                }
                t = type;
                while (t != null)
                {
                    var p = AccessTools.Property(t, "placingRot") ?? AccessTools.Property(t, "PlacingRot");
                    if (p != null && p.CanWrite && p.PropertyType == typeof(Rot4))
                    {
                        var set = p.GetSetMethod(true);
                        if (set != null)
                        {
                            var del = AccessTools.MethodDelegate<Action<Designator, Rot4>>(set);
                            return (des, rot) => del(des, rot);
                        }
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
            if (!GetRotCache.TryGetValue(type, out getter))
            {
                getter = BuildPlacingRotGetter(type);
                GetRotCache[type] = getter; // may be null if not found
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
                        var fieldRef = AccessTools.FieldRefAccess<Designator, Rot4>(f);
                        return des => fieldRef(des);
                    }
                    t = t.BaseType;
                }
                t = type;
                while (t != null)
                {
                    var p = AccessTools.Property(t, "placingRot") ?? AccessTools.Property(t, "PlacingRot");
                    if (p != null && p.CanRead && p.PropertyType == typeof(Rot4))
                    {
                        var get = p.GetGetMethod(true);
                        if (get != null)
                        {
                            var del = AccessTools.MethodDelegate<Func<Designator, Rot4>>(get);
                            return des => del(des);
                        }
                    }
                    t = t.BaseType;
                }
            }
            catch { }
            return null;
        }

        // True only when placing a building being reinstalled from the map (exclude MinifiedThing installs)
        public static bool IsReinstallDesignator(Designator d, out Thing source)
        {
            source = FindSourceThingForInstall(d);
            if (d == null) return false;

            if (InstanceCache.TryGetValue(d, out var cache) && cache.IsReinstall.HasValue)
            {
                return cache.IsReinstall.Value;
            }

            if (source == null) return false;

            try
            {
                // Reinstall when the source is an actual spawned building/object, NOT a MinifiedThing.
                // Minified items are often spawned in stockpiles; those should be treated as Install, not Reinstall.
                bool isReinstall = source.Spawned && !(source is MinifiedThing);

                if (InstanceCache.TryGetValue(d, out cache))
                {
                    cache.IsReinstall = isReinstall;
                }
                else
                {
                    InstanceCache.Add(d, new CacheData { IsReinstall = isReinstall });
                }
                return isReinstall;
            }
            catch { return false; }
        }

        public static Thing FindSourceThingForInstall(Designator d)
        {
            if (d == null) return null;
            var t = d.GetType();
            Func<Designator, Thing> getter;
            if (!InstallSourceCache.TryGetValue(t, out getter))
            {
                getter = BuildInstallSourceGetter(t) ?? (_ => null);
                InstallSourceCache[t] = getter;
            }
            try { return getter(d); } catch { return null; }
        }

        private static Func<Designator, Thing> BuildInstallSourceGetter(Type t)
        {
            try
            {
                // First try specific property names across the hierarchy
                var propCandidates = new[] { "MiniToInstallOrBuildingToReinstall", "ThingToInstall" };
                foreach (var propName in propCandidates)
                {
                    var tCur = t;
                    while (tCur != null)
                    {
                        var p = AccessTools.Property(tCur, propName);
                        if (p != null && p.CanRead && typeof(Thing).IsAssignableFrom(p.PropertyType))
                        {
                            var get = p.GetGetMethod(true);
                            if (get != null)
                            {
                                var param = System.Linq.Expressions.Expression.Parameter(typeof(Designator), "d");
                                var cast = System.Linq.Expressions.Expression.Convert(param, tCur);
                                var call = System.Linq.Expressions.Expression.Call(cast, get);
                                var asThing = System.Linq.Expressions.Expression.Convert(call, typeof(Thing));
                                return System.Linq.Expressions.Expression.Lambda<Func<Designator, Thing>>(asThing, param).Compile();
                            }
                        }
                        tCur = tCur.BaseType;
                    }
                }

                // Then try specific field names across the hierarchy
                var fieldCandidates = new[] { "thingToInstall", "ent", "installThing", "minifiedThing", "reinstall", "miniToInstallOrBuildingToReinstall" };
                foreach (var fieldName in fieldCandidates)
                {
                    var tCur = t;
                    while (tCur != null)
                    {
                        var f = AccessTools.Field(tCur, fieldName);
                        if (f != null && typeof(Thing).IsAssignableFrom(f.FieldType))
                        {
                            var param = System.Linq.Expressions.Expression.Parameter(typeof(Designator), "d");
                            var cast = System.Linq.Expressions.Expression.Convert(param, tCur);
                            var fld = System.Linq.Expressions.Expression.Field(cast, f);
                            var asThing = System.Linq.Expressions.Expression.Convert(fld, typeof(Thing));
                            return System.Linq.Expressions.Expression.Lambda<Func<Designator, Thing>>(asThing, param).Compile();
                        }
                        tCur = tCur.BaseType;
                    }
                }

                // Fallback: any readable property or field of type Thing across the hierarchy
                var tScan = t;
                while (tScan != null)
                {
                    foreach (var p in tScan.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!p.CanRead) continue;
                        if (!typeof(Thing).IsAssignableFrom(p.PropertyType)) continue;
                        var get = p.GetGetMethod(true);
                        if (get == null) continue;
                        var param = System.Linq.Expressions.Expression.Parameter(typeof(Designator), "d");
                        var cast = System.Linq.Expressions.Expression.Convert(param, tScan);
                        var call = System.Linq.Expressions.Expression.Call(cast, get);
                        var asThing = System.Linq.Expressions.Expression.Convert(call, typeof(Thing));
                        return System.Linq.Expressions.Expression.Lambda<Func<Designator, Thing>>(asThing, param).Compile();
                    }
                    foreach (var f in tScan.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!typeof(Thing).IsAssignableFrom(f.FieldType)) continue;
                        var param = System.Linq.Expressions.Expression.Parameter(typeof(Designator), "d");
                        var cast = System.Linq.Expressions.Expression.Convert(param, tScan);
                        var fld = System.Linq.Expressions.Expression.Field(cast, f);
                        var asThing = System.Linq.Expressions.Expression.Convert(fld, typeof(Thing));
                        return System.Linq.Expressions.Expression.Lambda<Func<Designator, Thing>>(asThing, param).Compile();
                    }
                    tScan = tScan.BaseType;
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
                if (thing is MinifiedThing direct) return direct.InnerThing;

                // Fallback: build and cache a compiled getter for this specific Thing type
                var t = thing.GetType();
                if (!InnerThingGetterCache.TryGetValue(t, out var getter) || getter == null)
                {
                    getter = BuildInnerThingGetter(t);
                    InnerThingGetterCache[t] = getter; // may be null
                }
                if (getter != null) return getter(thing);
            }
            catch { }
            return null;
        }

        private static Func<Thing, Thing> BuildInnerThingGetter(Type t)
        {
            try
            {
                if (t == null) return null;
                // Property named InnerThing
                var p = AccessTools.Property(t, "InnerThing");
                if (p != null && p.CanRead && typeof(Thing).IsAssignableFrom(p.PropertyType))
                {
                    var get = p.GetGetMethod(true);
                    if (get != null)
                    {
                        var del = AccessTools.MethodDelegate<Func<object, Thing>>(get);
                        return th => th != null ? del(th) : null;
                    }
                }
                // Field named innerThing (less common but safe)
                var f = AccessTools.Field(t, "innerThing") ?? AccessTools.Field(t, "InnerThing");
                if (f != null && typeof(Thing).IsAssignableFrom(f.FieldType))
                {
                    var paramObj = Expression.Parameter(typeof(Thing), "th");
                    var cast = Expression.Convert(paramObj, t);
                    var fld = Expression.Field(cast, f);
                    var asThing = Expression.Convert(fld, typeof(Thing));
                    return Expression.Lambda<Func<Thing, Thing>>(asThing, paramObj).Compile();
                }
            }
            catch { }
            return null;
        }

        public static Rot4 DirectionFromDelta(int dx, int dz)
        {
            if (dx == 0 && dz == 0) return default;
            return Math.Abs(dx) >= Math.Abs(dz)
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
                if (!TryGetPinned(des, out var pinCell))
                {
                    var curAtDown = GetActualMouseCell();
                    pinCell = curAtDown;
                    SetPinned(des, pinCell);
                    PlayPinSound(des);
                    DebugLog(() => $"Pin on MouseDown: des={des.GetType().Name}, cell={pinCell}");
                }
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
                        DebugLog(() => $"Rotate via mouse: des={des.GetType().Name}, rot={desired}");
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

                if (mouseMoved)
                {
                    LastMouseCell[des] = cur;
                    KeyboardOverrideUntilMove.Remove(des);

                    int dx = cur.x - pinned.x;
                    int dz = cur.z - pinned.z;
                    bool inDeadzone = Math.Abs(dx) <= MouseDeadzoneCells && Math.Abs(dz) <= MouseDeadzoneCells;

                    // Mouse rotation if outside deadzone and no active keyboard override
                    if (!inDeadzone && !KeyboardOverrideUntilMove.Contains(des))
                    {
                        if (dx != 0 || dz != 0)
                        {
                            var desired = DirectionFromDelta(dx, dz);
                            if (!TryGetPlacingRot(des, out var curRot) || curRot != desired)
                            {
                                SetAllPlacingRotFields(des, desired);
                                PlayRotateSound(des);
                                DebugLog(() => $"Rotate via mouse move: des={des.GetType().Name}, rot={desired}");
                            }
                        }
                    }
                }

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
                                    DebugLog(() => $"Rotate via keyboard: des={des.GetType().Name}, rot={newRot}");
                                }
                                KeyboardOverrideUntilMove.Add(des);
                            }
                        }
                    }
                }
                catch { }
            }

            bool released = (evt != null && evt.type == EventType.MouseUp && evt.button == 0) || Input.GetMouseButtonUp(0);
            if (released && TryGetPinned(des, out var toPlace))
            {
                var report = des.CanDesignateCell(toPlace);
                DebugLog(() => $"MouseUp: attempting placement. des={des.GetType().Name}, cell={toPlace}, accepted={report.Accepted}, reason='{report.Reason ?? ""}'");
                if (report.Accepted)
                {
                    des.DesignateSingleCell(toPlace);
                    // Vanilla success sound may not play in this deferred path; play it explicitly.
                    PlayDesignateSuccessSound(des);
                    UnmarkApplied(des);
                    DebugLog(() => "Placement executed via deferred designate.");
                }
                else
                {
                    // Show the rejection message after release on invalid location
                    try
                    {
                        if (!string.IsNullOrEmpty(report.Reason))
                        {
                            Messages.Message(report.Reason, MessageTypeDefOf.RejectInput, historical: false);
                            DebugLog(() => $"Rejected placement shown: '{report.Reason}'");
                        }
                    }
                    catch { }
                    // Do NOT unmark applied on failure; keep ghost rotation as last set.
                }
                // Always clear the pin state after handling placement/reject
                ClearPinned(des);
                LastMouseCell.Remove(des);
                KeyboardOverrideUntilMove.Remove(des);
                if (evt != null && evt.type == EventType.MouseUp && evt.button == 0) evt.Use();
                return true;
            }
            return false;
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
                // For installs, treat South as sentinel for 'no override'.
                if (s.installOverrideRotation == Rot4.South) return false;
                return ApplyOverrideOnce(des, s.installOverrideRotation);
            }
        }

        public static bool ApplyBuildOverrideIfNeeded(Designator des, PerfectPlacementSettings s)
        {
            if (des == null || s == null) return false;
            // South sentinel means no override for build
            if (s.buildOverrideRotation == Rot4.South) return false;
            if (!IsRotatable(des)) return false;
            return ApplyOverrideOnce(des, s.buildOverrideRotation);
        }

        public static bool HandleProcessInputEvents(DesignatorManager manager)
        {
            try
            {
                var evt = Event.current;
                if (manager == null || evt == null) return false;
                var settings = PerfectPlacement.Settings;
                if (settings == null) return false;
                var des = manager.SelectedDesignator;
                if (des == null) return false;
                if (!MouseRotateEnabledFor(des, settings) || !IsRotatable(des)) return false;

                if (evt.type == EventType.MouseDown && evt.button == 0)
                {
                    if (!TryGetPinned(des, out _))
                    {
                        var pin = GetActualMouseCell();
                        SetPinned(des, pin);
                        PlayPinSound(des);
                        DebugLog(() => $"Pin via ProcessInputEvents: des={des.GetType().Name}, cell={pin}");
                    }
                    evt.Use();
                    return true;
                }

                if (evt.button == 0 && TryGetPinned(des, out _))
                {
                    if (evt.type == EventType.MouseDrag || evt.type == EventType.MouseUp)
                    {
                        evt.Use();
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static void HandleDesignatorUpdate(Designator des)
        {
            var settings = PerfectPlacement.Settings;
            if (des == null || settings == null) return;

            if (des is Designator_Install)
            {
                HandleInstallUpdate(des, settings);
                return;
            }

            if (des is Designator_Build)
            {
                HandleBuildUpdate(des, settings);
                return;
            }

            if (MouseRotateEnabledFor(des, settings))
            {
                HandleMouseRotate(des);
            }
        }

        private static void HandleBuildUpdate(Designator des, PerfectPlacementSettings settings)
        {
            bool buildOverrideActive = settings.buildOverrideRotation != Rot4.South;
            bool rotateActive = MouseRotateEnabledFor(des, settings);
            if (!buildOverrideActive && !rotateActive) return;

            if (buildOverrideActive)
            {
                ApplyBuildOverrideIfNeeded(des, settings);
            }

            HandleMouseRotate(des);
        }

        private static void HandleInstallUpdate(Designator des, PerfectPlacementSettings settings)
        {
            bool isReinstall = IsReinstallDesignator(des, out var source);
            bool mouseRotate = MouseRotateEnabledFor(des, settings);

            bool anyActive = isReinstall
                ? (settings.useOverrideRotation || settings.PerfectPlacement || mouseRotate)
                : (settings.installOverrideRotation != Rot4.South || mouseRotate);

            if (!anyActive) return;

            if (ApplyInstallOrReinstallOverrideIfNeeded(des, settings, isReinstall)) return;

            if (isReinstall && settings.PerfectPlacement && !WasApplied(des))
            {
                if (IsRotatable(des))
                {
                    var desiredKeep = source.Rotation;
                    if (SetAllPlacingRotFields(des, desiredKeep))
                    {
                        MarkApplied(des);
                    }
                    else
                    {
                        DebugLog(() => $"KeepRotation: failed to set placingRot for {des.GetType().Name}");
                    }
                }
                else
                {
                    DebugLog(() => $"KeepRotation: not rotatable or null source. src={(source == null ? "<null>" : source.def.defName)}");
                }
            }

            HandleMouseRotate(des);
        }

        private static SoundDef ResolveSound(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return null;
            try
            {
                if (SoundCache.TryGetValue(defName, out var sd)) return sd;
                var resolved = DefDatabase<SoundDef>.GetNamedSilentFail(defName);
                SoundCache[defName] = resolved; // cache even if null
                return resolved;
            }
            catch { return null; }
        }

        // Removed: AllowNextSuccessSound — no global sound suppression remains

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
                    {
                        var param = System.Linq.Expressions.Expression.Parameter(typeof(Designator), "d");
                        var cast = System.Linq.Expressions.Expression.Convert(param, tCur);
                        var fld = System.Linq.Expressions.Expression.Field(cast, f);
                        var asSd = System.Linq.Expressions.Expression.Convert(fld, typeof(SoundDef));
                        return System.Linq.Expressions.Expression.Lambda<Func<Designator, SoundDef>>(asSd, param).Compile();
                    }
                    tCur = tCur.BaseType;
                }
                tCur = t;
                while (tCur != null)
                {
                    var p = AccessTools.Property(tCur, "soundSucceeded");
                    if (p != null && p.CanRead && typeof(SoundDef).IsAssignableFrom(p.PropertyType))
                    {
                        var get = p.GetGetMethod(true);
                        if (get != null)
                        {
                            var param = System.Linq.Expressions.Expression.Parameter(typeof(Designator), "d");
                            var cast = System.Linq.Expressions.Expression.Convert(param, tCur);
                            var call = System.Linq.Expressions.Expression.Call(cast, get);
                            var asSd = System.Linq.Expressions.Expression.Convert(call, typeof(SoundDef));
                            return System.Linq.Expressions.Expression.Lambda<Func<Designator, SoundDef>>(asSd, param).Compile();
                        }
                    }
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
            if (!SuccessSoundGetterCache.TryGetValue(t, out getter))
            {
                getter = BuildSuccessSoundGetter(t);
                SuccessSoundGetterCache[t] = getter; // may be null
            }
            try { return getter != null ? getter(des) : null; } catch { return null; }
        }

        public static void PlayDesignateSuccessSound(Designator des)
        {
            try
            {
                SoundDef sd = TryGetDesignatorSuccessSound(des);
                if (sd == null) sd = ResolveSound("Designate_Place");
                if (sd != null)
                {
                    var map = des?.Map ?? Find.CurrentMap;
                    SoundStarter.PlayOneShotOnCamera(sd, map);
                }
            }
            catch { }
        }

        // Removed: ShouldSuppressPlacementSound since global sound suppression is no longer used

        public static void PlayPinSound(Designator des)
        {
            try
            {
                var sd = ResolveSound("Designate_DragBuilding_Start");
                if (sd != null)
                {
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
                // Use static UI drag slider sound for rotation feedback
                SoundDefOf.DragSlider.PlayOneShotOnCamera();
            }
            catch { }
        }

    }
}
