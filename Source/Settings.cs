﻿using UnityEngine;
using Verse;
using static ResearchPal.ResourceBank.String;

namespace ResearchPal
{
  public class Settings : ModSettings
    {
        #region tuning parameters

        public static bool shouldPause;
        public static bool shouldReset;
        public static bool shouldSeparateByTechLevels;

        public static int largeModTechCount = 5;

        #endregion tuning parameters

        public static void DoSettingsWindowContents(Rect rect)
        {
            Listing_Standard list = new Listing_Standard(GameFont.Small);
            list.ColumnWidth = rect.width;
            list.Begin(rect);

            list.CheckboxLabeled(ShouldSeparateByTechLevels, ref shouldSeparateByTechLevels,
                                 ShouldSeparateByTechLevelsTip);
            list.Gap();

            list.CheckboxLabeled(ShouldPauseOnOpen, ref shouldPause,
                                  ShouldPauseOnOpenTip);
            list.CheckboxLabeled(ShouldResetOnOpen, ref shouldReset,
                                  ShouldResetOnOpenTip);
            list.End();
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref shouldSeparateByTechLevels, "ShouldSeparateByTechLevels", false);
            Scribe_Values.Look(ref shouldPause, "ShouldPauseOnOpen", true);
            Scribe_Values.Look(ref shouldReset, "ShouldResetOnOpen", false);
        }
    }
}