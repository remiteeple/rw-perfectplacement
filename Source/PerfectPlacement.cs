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

        public override string SettingsCategory() => "Placement Plus";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);

            // --- GLOBAL SETTINGS ---
            list.CheckboxLabeled("Enable Mouse Rotation", ref Settings.globalMouseRotate, "Hold left-click to pin an object and rotate it with the mouse.");
            list.CheckboxLabeled("Enable Debug Logs", ref Settings.debugLogs, "Log suppression and placement flow details to help diagnose issues.");
            list.GapLine();

            // --- REINSTALL ---
            Text.Font = GameFont.Medium;
            list.Label("When Reinstalling an object:");
            Text.Font = GameFont.Small;
            list.Gap(6f);

            bool keep = Settings.PerfectPlacement;
            bool reinstallOverride = Settings.useOverrideRotation;

            if (list.RadioButton("Keep original rotation", keep, tooltip: "When reinstalling, the object will keep its current rotation."))
            {
                Settings.PerfectPlacement = true;
                Settings.useOverrideRotation = false;
            }

            if (list.RadioButton("Use override rotation", reinstallOverride, tooltip: "When reinstalling, always force the object to a specific rotation."))
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
            list.Label("When Installing a minified object:");
            Text.Font = GameFont.Small;
            list.Gap(6f);

            list.CheckboxLabeled("Use override rotation", ref Settings.installUseOverrideRotation, "When installing, always force the object to a specific rotation.");
            if (Settings.installUseOverrideRotation)
            {
                DrawRotationWidget(list, Settings.installOverrideRotation, newRot => Settings.installOverrideRotation = newRot);
            }

            list.GapLine();

            // --- BUILD ---
            Text.Font = GameFont.Medium;
            list.Label("When Building from the Architect menu:");
            Text.Font = GameFont.Small;
            list.Gap(6f);

            list.CheckboxLabeled("Use override rotation", ref Settings.buildUseOverrideRotation, "When building, always force the object to a specific rotation.");
            if (Settings.buildUseOverrideRotation)
            {
                DrawRotationWidget(list, Settings.buildOverrideRotation, newRot => Settings.buildOverrideRotation = newRot);
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
            Widgets.Label(row.LeftHalf(), "Override direction:");
            Rect buttonRect = row.RightHalf();
            string label = currentRotation.ToStringHuman();
            if (Widgets.ButtonText(buttonRect, label))
            {
                var opts = new List<FloatMenuOption>
                {
                    new FloatMenuOption("North", () => setter(Rot4.North)),
                    new FloatMenuOption("East",  () => setter(Rot4.East)),
                    new FloatMenuOption("South", () => setter(Rot4.South)),
                    new FloatMenuOption("West",  () => setter(Rot4.West))
                };
                Find.WindowStack.Add(new FloatMenu(opts));
            }
        }
    }
}
