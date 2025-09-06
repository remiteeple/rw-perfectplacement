using Verse;

namespace PerfectPlacement
{
    public class PerfectPlacementSettings : ModSettings
    {
        // Global
        public bool globalMouseRotate = true;
        public bool debugLogs = false;

        // Reinstall
        public bool PerfectPlacement = true;
        public bool useOverrideRotation = false;
        public Rot4 overrideRotation = Rot4.South;

        // Install
        public bool installUseOverrideRotation = false;
        public Rot4 installOverrideRotation = Rot4.South;

        // Build
        public bool buildUseOverrideRotation = false;
        public Rot4 buildOverrideRotation = Rot4.South;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref globalMouseRotate, nameof(globalMouseRotate), true);
            Scribe_Values.Look(ref debugLogs, nameof(debugLogs), false);

            Scribe_Values.Look(ref PerfectPlacement, nameof(PerfectPlacement), true);
            Scribe_Values.Look(ref useOverrideRotation, nameof(useOverrideRotation), false);
            Scribe_Values.Look(ref overrideRotation, nameof(overrideRotation), Rot4.South);

            Scribe_Values.Look(ref installUseOverrideRotation, nameof(installUseOverrideRotation), false);
            Scribe_Values.Look(ref installOverrideRotation, nameof(installOverrideRotation), Rot4.South);

            Scribe_Values.Look(ref buildUseOverrideRotation, nameof(buildUseOverrideRotation), false);
            Scribe_Values.Look(ref buildOverrideRotation, nameof(buildOverrideRotation), Rot4.South);
        }
    }
}
