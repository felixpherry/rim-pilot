using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimPilot.Mod.Bridge
{
    // Finite named reads matching protocol/developer-safety-v1.json. No observation endpoint,
    // virtual Window.IsDebug/title/getter calls, settings writes, or generic object projection.
    internal sealed class GameDeveloperSafety : IDeveloperSafetySource<Window>
    {
        private readonly AccessTools.FieldRef<bool>? showAllHediffs, showHediffsDebugInfo;
        internal GameDeveloperSafety()
        {
            // Metadata-only, two literal private names. Cached delegates perform named reads;
            // no FieldInfo.GetValue, arbitrary field lookup, traversal, or per-sample binding.
            // Failure remains unknown, never a false baseline. Bootstrap binds metadata only;
            // neither delegate is invoked here. Sampling invokes them only on game thread.
            try
            {
                showAllHediffs = BindHealthField("showAllHediffs");
                showHediffsDebugInfo = BindHealthField("showHediffsDebugInfo");
            }
            catch { }
        }
        private static AccessTools.FieldRef<bool> BindHealthField(string name)
        {
            FieldInfo? field = typeof(HealthCardUtility).GetField(name,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field == null || !field.IsStatic || field.FieldType != typeof(bool) || field.IsLiteral)
                throw new InvalidOperationException();
            return AccessTools.StaticFieldRefAccess<bool>(field);
        }
        public bool DeveloperMode => Prefs.DevMode;
        public bool CanInspect => showAllHediffs != null && showHediffsDebugInfo != null
            && !LongEventHandler.AnyEventNowOrWaiting && Current.Root != null
            && Current.Root.uiRoot != null && Current.Root.uiRoot.windows != null
            // Audited cached-string getter, not normal-version formatting/drawing.
            && VersionControl.CurrentVersionStringWithRev == "1.6.4871 rev600";
        public int StaticReadCount => 185;
        // Audited nonvirtual WindowStack accessors read only its List<Window> Count/indexer.
        public int WindowCount => Current.Root.uiRoot.windows.Count;
        public Window WindowAt(int index) => Current.Root.uiRoot.windows[index];
        public bool KnownDeveloperWindow(Window window) =>
            window is LudeonTK.Window_Dev ||
            window is LudeonTK.Dialog_Debug ||
            window is LudeonTK.EditWindow_DebugInspector ||
            window is LudeonTK.EditWindow_TweakValues ||
            window is LudeonTK.EditWindow_Log ||
            window is LudeonTK.Dialog_DevPalette ||
            window is Verse.Dialog_DebugSetSeverity ||
            window is Verse.Dialog_DebugSetHediffRemaining ||
            window is Verse.Dialog_PawnTableTest ||
            window is RimWorld.Dialog_DebugRenderTree ||
            window is Verse.Dialog_CameraConfig ||
            window is Verse.Dialog_CameraConfigList;

        public bool BaselineMatches(int index)
        {
            switch (index)
            {
                case 0: return Verse.DebugSettings.godMode == false;
                case 1: return Verse.DebugSettings.devPalette == false;
                case 2: return Verse.DebugSettings.showHiddenPawns == false;
                case 3: return Verse.DebugSettings.showHiddenInfo == false;
                case 4: return Verse.DebugSettings.showLocomotionUrgency == false;
                case 5: return Verse.DebugSettings.searchIgnoresRestrictions == false;
                case 6: return Verse.DebugSettings.enableDamage == true;
                case 7: return Verse.DebugSettings.enablePlayerDamage == true;
                case 8: return Verse.DebugSettings.enableRandomMentalStates == true;
                case 9: return Verse.DebugSettings.enableStoryteller == true;
                case 10: return Verse.DebugSettings.enableRandomDiseases == true;
                case 11: return Verse.DebugSettings.enableTranslationWindowInEnglish == false;
                case 12: return Verse.DebugSettings.pauseOnError == false;
                case 13: return Verse.DebugSettings.noAnimals == false;
                case 14: return Verse.DebugSettings.unlimitedPower == false;
                case 15: return Verse.DebugSettings.pathThroughWalls == false;
                case 16: return Verse.DebugSettings.instantRecruit == false;
                case 17: return Verse.DebugSettings.alwaysSocialFight == false;
                case 18: return Verse.DebugSettings.alwaysDoLovin == false;
                case 19: return Verse.DebugSettings.detectRegionListersBugs == false;
                case 20: return Verse.DebugSettings.instantVisitorsGift == false;
                case 21: return Verse.DebugSettings.lowFPS == false;
                case 22: return Verse.DebugSettings.allowUndraftedMechOrders == false;
                case 23: return Verse.DebugSettings.editableGlowerColors == false;
                case 24: return Verse.DebugSettings.anomalyDarkeningFX == true;
                case 25: return Verse.DebugSettings.fastResearch == false;
                case 26: return Verse.DebugSettings.fastLearning == false;
                case 27: return Verse.DebugSettings.fastEcology == false;
                case 28: return Verse.DebugSettings.fastEcologyRegrowRateOnly == false;
                case 29: return Verse.DebugSettings.fastCrafting == false;
                case 30: return Verse.DebugSettings.fastCaravans == false;
                case 31: return Verse.DebugSettings.fastMapUnpollution == false;
                case 32: return Verse.DebugSettings.activateAllBuildingDemands == false;
                case 33: return Verse.DebugSettings.activateAllIdeoRoles == false;
                case 34: return Verse.DebugSettings.playRitualAmbience == true;
                case 35: return Verse.DebugSettings.simulateUsingSteamDeck == false;
                case 36: return Verse.DebugSettings.logRaidInfo == false;
                case 37: return Verse.DebugSettings.logTranslationLookupErrors == false;
                case 38: return Verse.DebugSettings.logPsychicRitualTransitions == false;
                case 39: return Verse.DebugSettings.fastMonolithRespawn == false;
                case 40: return Verse.DebugSettings.alwaysRareCatches == false;
                case 41: return Verse.DebugSettings.alwaysNegativeCatches == false;
                case 42: return Verse.DebugSettings.logMismatchedLayoutFactions == false;
                case 43: return Verse.DebugSettings.loopGravshipCutscene == false;
                case 44: return Verse.DebugSettings.skipGravshipTileSelection == false;
                case 45: return Verse.DebugSettings.ignoreGravshipRange == false;
                case 46: return Verse.DebugViewSettings.drawFog == true;
                case 47: return Verse.DebugViewSettings.drawSnow == true;
                case 48: return Verse.DebugViewSettings.drawSand == true;
                case 49: return Verse.DebugViewSettings.drawTerrain == true;
                case 50: return Verse.DebugViewSettings.drawTerrainWater == true;
                case 51: return Verse.DebugViewSettings.drawThingsDynamic == true;
                case 52: return Verse.DebugViewSettings.drawThingsPrinted == true;
                case 53: return Verse.DebugViewSettings.drawShadows == true;
                case 54: return Verse.DebugViewSettings.drawLightingOverlay == true;
                case 55: return Verse.DebugViewSettings.drawWorldOverlays == true;
                case 56: return Verse.DebugViewSettings.drawGas == true;
                case 57: return Verse.DebugViewSettings.drawWorldObjects == true;
                case 58: return Verse.DebugViewSettings.drawPaths == false;
                case 59: return Verse.DebugViewSettings.drawPatherState == false;
                case 60: return Verse.DebugViewSettings.drawCastPositionSearch == false;
                case 61: return Verse.DebugViewSettings.drawDestSearch == false;
                case 62: return Verse.DebugViewSettings.drawStyleSearch == false;
                case 63: return Verse.DebugViewSettings.drawSectionEdges == false;
                case 64: return Verse.DebugViewSettings.drawRiverDebug == false;
                case 65: return Verse.DebugViewSettings.drawRiverFlowDebug == false;
                case 66: return Verse.DebugViewSettings.drawPawnDebug == false;
                case 67: return Verse.DebugViewSettings.drawPawnRotatorTarget == false;
                case 68: return Verse.DebugViewSettings.drawRegions == false;
                case 69: return Verse.DebugViewSettings.drawRegionLinks == false;
                case 70: return Verse.DebugViewSettings.drawRegionDirties == false;
                case 71: return Verse.DebugViewSettings.drawRegionTraversal == false;
                case 72: return Verse.DebugViewSettings.drawRegionThings == false;
                case 73: return Verse.DebugViewSettings.drawDistricts == false;
                case 74: return Verse.DebugViewSettings.drawRooms == false;
                case 75: return Verse.DebugViewSettings.drawPower == false;
                case 76: return Verse.DebugViewSettings.drawPowerNetGrid == false;
                case 77: return Verse.DebugViewSettings.drawOpportunisticJobs == false;
                case 78: return Verse.DebugViewSettings.drawTooltipEdges == false;
                case 79: return Verse.DebugViewSettings.drawRecordedNoise == false;
                case 80: return Verse.DebugViewSettings.drawFoodSearchFromMouse == false;
                case 81: return Verse.DebugViewSettings.drawPreyInfo == false;
                case 82: return Verse.DebugViewSettings.drawGlow == false;
                case 83: return Verse.DebugViewSettings.drawAvoidGrid == false;
                case 84: return Verse.DebugViewSettings.drawBreachingGrid == false;
                case 85: return Verse.DebugViewSettings.drawBreachingNoise == false;
                case 86: return Verse.DebugViewSettings.drawLords == false;
                case 87: return Verse.DebugViewSettings.drawDuties == false;
                case 88: return Verse.DebugViewSettings.drawShooting == false;
                case 89: return Verse.DebugViewSettings.drawInfestationChance == false;
                case 90: return Verse.DebugViewSettings.drawFleshmassHeartChance == false;
                case 91: return Verse.DebugViewSettings.drawStealDebug == false;
                case 92: return Verse.DebugViewSettings.drawDeepResources == false;
                case 93: return Verse.DebugViewSettings.drawAttackTargetScores == false;
                case 94: return Verse.DebugViewSettings.drawFOVSymmetry == false;
                case 95: return Verse.DebugViewSettings.drawNonCombatantTimer == false;
                case 96: return Verse.DebugViewSettings.drawInteractionCells == false;
                case 97: return Verse.DebugViewSettings.drawDoorsDebug == false;
                case 98: return Verse.DebugViewSettings.drawDestReservations == false;
                case 99: return Verse.DebugViewSettings.drawDamageRects == false;
                case 100: return Verse.DebugViewSettings.drawDissolutionCells == false;
                case 101: return Verse.DebugViewSettings.drawUnpollutionCells == false;
                case 102: return Verse.DebugViewSettings.drawHateChanterPositions == false;
                case 103: return Verse.DebugViewSettings.drawDarknessOverlay == true;
                case 104: return Verse.DebugViewSettings.drawWoundAnchorsOnHover == false;
                case 105: return Verse.DebugViewSettings.drawMapGraphs == false;
                case 106: return Verse.DebugViewSettings.drawMapRooms == false;
                case 107: return Verse.DebugViewSettings.drawIndoorMask == false;
                case 108: return Verse.DebugViewSettings.drawOutdoorMask == false;
                case 109: return Verse.DebugViewSettings.drawShamblerAlertMote == true;
                case 110: return Verse.DebugViewSettings.drawWaterBodies == false;
                case 111: return Verse.DebugViewSettings.drawMeltingIce == false;
                case 112: return Verse.DebugViewSettings.drawUsedRects == false;
                case 113: return Verse.DebugViewSettings.drawRoadPaths == false;
                case 114: return Verse.DebugViewSettings.drawGravshipMask == false;
                case 115: return Verse.DebugViewSettings.drawTerrainCurtain == true;
                case 116: return Verse.DebugViewSettings.writeGame == false;
                case 117: return Verse.DebugViewSettings.writeSteamItems == false;
                case 118: return Verse.DebugViewSettings.writeConcepts == false;
                case 119: return Verse.DebugViewSettings.writeReservations == false;
                case 120: return Verse.DebugViewSettings.writePathCosts == false;
                case 121: return Verse.DebugViewSettings.writeFertility == false;
                case 122: return Verse.DebugViewSettings.writeLinkFlags == false;
                case 123: return Verse.DebugViewSettings.writeCover == false;
                case 124: return Verse.DebugViewSettings.writeCellContents == false;
                case 125: return Verse.DebugViewSettings.writeMusicManagerPlay == false;
                case 126: return Verse.DebugViewSettings.writeStoryteller == false;
                case 127: return Verse.DebugViewSettings.writePlayingSounds == false;
                case 128: return Verse.DebugViewSettings.writeSoundEventsRecord == false;
                case 129: return Verse.DebugViewSettings.writeMoteSaturation == false;
                case 130: return Verse.DebugViewSettings.writeSnowDepth == false;
                case 131: return Verse.DebugViewSettings.writeSandDepth == false;
                case 132: return Verse.DebugViewSettings.writeEcosystem == false;
                case 133: return Verse.DebugViewSettings.writeRecentStrikes == false;
                case 134: return Verse.DebugViewSettings.writeBeauty == false;
                case 135: return Verse.DebugViewSettings.writeListRepairableBldgs == false;
                case 136: return Verse.DebugViewSettings.writeListFilthInHomeArea == false;
                case 137: return Verse.DebugViewSettings.writeListHaulables == false;
                case 138: return Verse.DebugViewSettings.writeListMergeables == false;
                case 139: return Verse.DebugViewSettings.writeTotalSnowDepth == false;
                case 140: return Verse.DebugViewSettings.writeCanReachColony == false;
                case 141: return Verse.DebugViewSettings.writeMentalStateCalcs == false;
                case 142: return Verse.DebugViewSettings.writeWind == false;
                case 143: return Verse.DebugViewSettings.writeTerrain == false;
                case 144: return Verse.DebugViewSettings.writeApparelScore == false;
                case 145: return Verse.DebugViewSettings.writeWorkSettings == false;
                case 146: return Verse.DebugViewSettings.writeSkyManager == false;
                case 147: return Verse.DebugViewSettings.writeMemoryUsage == false;
                case 148: return Verse.DebugViewSettings.writeMapGameConditions == false;
                case 149: return Verse.DebugViewSettings.writeAttackTargets == false;
                case 150: return Verse.DebugViewSettings.writeRopesAndPens == false;
                case 151: return Verse.DebugViewSettings.writeRoomRoles == false;
                case 152: return Verse.DebugViewSettings.debugApparelOptimize == false;
                case 153: return Verse.DebugViewSettings.disableGravshipRenderShader == false;
                case 154: return Verse.DebugViewSettings.showAllRoomStats == false;
                case 155: return Verse.DebugViewSettings.showFloatMenuWorkGivers == false;
                case 156: return Verse.DebugViewSettings.showArchitectMenuOrder == false;
                case 157: return Verse.DebugViewSettings.showTpsCounter == false;
                case 158: return Verse.DebugViewSettings.showFpsCounter == false;
                case 159: return Verse.DebugViewSettings.showMemoryInfo == false;
                case 160: return Verse.DebugViewSettings.singleThreadedDrawing == false;
                case 161: return Verse.DebugViewSettings.logIncapChance == false;
                case 162: return Verse.DebugViewSettings.logInput == false;
                case 163: return Verse.DebugViewSettings.logApparelGeneration == false;
                case 164: return Verse.DebugViewSettings.logLordToilTransitions == false;
                case 165: return Verse.DebugViewSettings.logGrammarResolution == false;
                case 166: return Verse.DebugViewSettings.logCombatLogMouseover == false;
                case 167: return Verse.DebugViewSettings.logCauseOfDeath == false;
                case 168: return Verse.DebugViewSettings.logMapLoad == false;
                case 169: return Verse.DebugViewSettings.logTutor == false;
                case 170: return Verse.DebugViewSettings.logSignals == false;
                case 171: return Verse.DebugViewSettings.logWorldPawnGC == false;
                case 172: return Verse.DebugViewSettings.logTaleRecording == false;
                case 173: return Verse.DebugViewSettings.logHourlyScreenshot == false;
                case 174: return Verse.DebugViewSettings.logFilthSummary == false;
                case 175: return Verse.DebugViewSettings.logCarriedBetweenJobs == false;
                case 176: return Verse.DebugViewSettings.logComplexGenPoints == false;
                case 177: return Verse.DebugViewSettings.saveGravshipRenders == false;
                case 178: return Verse.DebugViewSettings.neverForceNormalSpeed == false;
                case 179: return showAllHediffs!() == false;
                case 180: return showHediffsDebugInfo!() == false;
                case 181: return Verse.DebugGenerationSettings.prefabCopyTerrain == false;
                case 182: return Verse.DebugGenerationSettings.prefabCopyAllThings == false;
                case 183: return LudeonTK.DebugTools.curTool == null;
                case 184: return LudeonTK.DebugTools.curMeasureTool == null;
                default: throw new InvalidOperationException();
            }
        }
    }
}
