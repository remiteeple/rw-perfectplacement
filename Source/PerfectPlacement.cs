using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PerfectPlacement
{
    public class PerfectPlacement : Mod
    {
        public static PerfectPlacementSettings Settings { get; private set; }

        public PerfectPlacement(ModContentPack content) : base(content)
        {
            Settings = GetSettings<PerfectPlacementSettings>();
        }

        public override string SettingsCategory() => "PP.SettingsCategory".Translate();

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);

            // --- GLOBAL SETTINGS ---
            list.CheckboxLabeled(
                "PP.EnableMouseRotation".Translate(),
                ref Settings.globalMouseRotate,
                "PP.EnableMouseRotation.Desc".Translate());
            list.CheckboxLabeled(
                "PP.EnableDebugLogs".Translate(),
                ref Settings.debugLogs,
                "PP.EnableDebugLogs.Desc".Translate());
            list.GapLine();

            // --- REINSTALL ---
            Text.Font = GameFont.Medium;
            list.Label("PP.Section.Reinstall".Translate());
            Text.Font = GameFont.Small;
            list.Gap(6f);

            bool keep = Settings.PerfectPlacement;
            bool reinstallOverride = Settings.useOverrideRotation;

            if (list.RadioButton("PP.Option.KeepOriginalRotation".Translate(), keep, tooltip: "PP.Option.KeepOriginalRotation.Desc".Translate()))
            {
                Settings.PerfectPlacement = true;
                Settings.useOverrideRotation = false;
            }

            if (list.RadioButton("PP.Option.ReinstallDirection".Translate(), reinstallOverride, tooltip: "PP.Option.ReinstallDirection.Desc".Translate()))
            {
                Settings.PerfectPlacement = false;
                Settings.useOverrideRotation = true;
            }

            if (Settings.useOverrideRotation)
            {
                DrawRotationWidget(list, Settings.overrideRotation, newRot => Settings.overrideRotation = newRot);
            }

            list.GapLine();

            // --- INSTALL ---
            Text.Font = GameFont.Medium;
            list.Label("PP.Section.Install".Translate());
            Text.Font = GameFont.Small;
            list.Gap(6f);

            // Always show direction selection. Selecting South means no override.
            Rect row = list.GetRect(Text.LineHeight);
            row.xMin += 0f; // no extra indent beyond section
            Widgets.Label(row.LeftHalf(), "PP.Label.InstallDirection".Translate());
            Rect buttonRect = row.RightHalf();
            string label = Settings.installOverrideRotation.ToStringHuman();
            if (Widgets.ButtonText(buttonRect, label))
            {
                var opts = new List<FloatMenuOption>
                {
                    new FloatMenuOption(Rot4.North.ToStringHuman(), () => Settings.installOverrideRotation = Rot4.North),
                    new FloatMenuOption(Rot4.East.ToStringHuman(),  () => Settings.installOverrideRotation = Rot4.East),
                    new FloatMenuOption(Rot4.South.ToStringHuman(), () => Settings.installOverrideRotation = Rot4.South),
                    new FloatMenuOption(Rot4.West.ToStringHuman(),  () => Settings.installOverrideRotation = Rot4.West)
                };
                Find.WindowStack.Add(new FloatMenu(opts));
            }

            list.GapLine();

            // --- BUILD ---
            Text.Font = GameFont.Medium;
            list.Label("PP.Section.Build".Translate());
            Text.Font = GameFont.Small;
            list.Gap(6f);

            // Always show direction selection. Selecting South means no override.
            Rect buildRow = list.GetRect(Text.LineHeight);
            buildRow.xMin += 0f;
            Widgets.Label(buildRow.LeftHalf(), "PP.Label.BuildDirection".Translate());
            Rect buildButtonRect = buildRow.RightHalf();
            string buildLabel = Settings.buildOverrideRotation.ToStringHuman();
            if (Widgets.ButtonText(buildButtonRect, buildLabel))
            {
                var opts = new List<FloatMenuOption>
                {
                    new FloatMenuOption(Rot4.North.ToStringHuman(), () => Settings.buildOverrideRotation = Rot4.North),
                    new FloatMenuOption(Rot4.East.ToStringHuman(),  () => Settings.buildOverrideRotation = Rot4.East),
                    new FloatMenuOption(Rot4.South.ToStringHuman(), () => Settings.buildOverrideRotation = Rot4.South),
                    new FloatMenuOption(Rot4.West.ToStringHuman(),  () => Settings.buildOverrideRotation = Rot4.West)
                };
                Find.WindowStack.Add(new FloatMenu(opts));
            }

            list.GapLine();

            // --- BUILD COPY ---
            Text.Font = GameFont.Medium;
            list.Label("PP.Section.BuildCopy".Translate());
            Text.Font = GameFont.Small;
            list.Gap(6f);

            bool buildCopyKeep = Settings.buildCopyMode == BuildCopyRotationMode.KeepSource;
            bool buildCopyOverride = Settings.buildCopyMode == BuildCopyRotationMode.Override;

            if (list.RadioButton("PP.Option.BuildCopy.Keep".Translate(), buildCopyKeep, tooltip: "PP.Option.BuildCopy.Keep.Desc".Translate()))
            {
                Settings.buildCopyMode = BuildCopyRotationMode.KeepSource;
            }

            if (list.RadioButton("PP.Option.BuildCopy.Override".Translate(), buildCopyOverride, tooltip: "PP.Option.BuildCopy.Override.Desc".Translate()))
            {
                Settings.buildCopyMode = BuildCopyRotationMode.Override;
            }

            if (Settings.buildCopyMode == BuildCopyRotationMode.Override)
            {
                DrawRotationWidget(list, Settings.buildCopyOverrideRotation, newRot => Settings.buildCopyOverrideRotation = newRot);
            }
            list.End();
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            try
            {
                Log.Message($"[PerfectPlacement] Debug logs now {(Settings.debugLogs ? "ENABLED" : "disabled")}.");
            }
            catch { }
        }

        private void DrawRotationWidget(Listing_Standard list, Rot4 currentRotation, System.Action<Rot4> setter)
        {
            Rect row = list.GetRect(Text.LineHeight);
            row.xMin += 24f; // Indent
            Widgets.Label(row.LeftHalf(), "PP.Label.OverrideDirection".Translate());
            Rect buttonRect = row.RightHalf();
            string label = currentRotation.ToStringHuman();
            if (Widgets.ButtonText(buttonRect, label))
            {
                var opts = new List<FloatMenuOption>
                {
                    new FloatMenuOption(Rot4.North.ToStringHuman(), () => setter(Rot4.North)),
                    new FloatMenuOption(Rot4.East.ToStringHuman(),  () => setter(Rot4.East)),
                    new FloatMenuOption(Rot4.South.ToStringHuman(), () => setter(Rot4.South)),
                    new FloatMenuOption(Rot4.West.ToStringHuman(),  () => setter(Rot4.West))
                };
                Find.WindowStack.Add(new FloatMenu(opts));
            }
        }
    }
}

