using HarmonyLib;
using Godot;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Achievements;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.TreasureRooms;
using MegaCrit.Sts2.Core.Leaderboard;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.Metrics;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Rewards;

namespace Sts2SpireRace.Replay;

[HarmonyPatch]
internal static class HarmonyPatches
{
    [System.ThreadStatic]
    private static bool _insideTreasureProceed;

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.InitProfileId))]
    [HarmonyPostfix]
    private static void AfterProfileInitialized()
    {
        ReplayMod.TryInitialize();
    }

    [HarmonyPatch(typeof(RunManager), "InitializeShared")]
    [HarmonyPostfix]
    private static void AfterRunManagerInitialized()
    {
        ReplayMod.Recorder?.AttachRunHooks();
        BranchSaveRouter.AttachRunHooks();
    }

    [HarmonyPatch(typeof(CombatReplayWriter), nameof(CombatReplayWriter.WriteReplay))]
    [HarmonyPrefix]
    private static void BeforeNativeReplayWrite(bool stopRecording)
    {
        if (!stopRecording) return;
        ReplayMod.Recorder?.FlushPartial();
        BranchSaveRouter.FlushActiveCombat();
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
    [HarmonyPrefix]
    private static void BeforeRunCleanup()
    {
        ReplayMod.Recorder?.FinalizeActiveAsIncomplete();
        BranchSaveRouter.FlushActiveCombat();
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
    [HarmonyPrefix]
    private static void BeforeRunEnded(bool isVictory)
    {
        ReplayMod.Recorder?.FinalizeRun(isVictory ? "WIN" : (RunManager.Instance.IsAbandoned ? "ABANDONED" : "LOSS"));
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
    [HarmonyFinalizer]
    private static void AfterRunCleanup()
    {
        ReplayMod.ResetRuntimeMode();
    }

    [HarmonyPatch(typeof(NClickableControl), nameof(NClickableControl._GuiInput))]
    [HarmonyPrefix]
    private static bool BlockReadOnlyClickableInput(NClickableControl __instance, InputEvent inputEvent)
    {
        if (!ReplayUiInteractionPolicy.ShouldBlock(__instance)) return true;
        __instance.AcceptEvent();
        return false;
    }

    [HarmonyPatch(typeof(NCardHolder), nameof(NCardHolder._GuiInput))]
    [HarmonyPrefix]
    private static bool BlockReadOnlyCardInput(NCardHolder __instance, InputEvent inputEvent)
    {
        if (!ReplayUiInteractionPolicy.ShouldBlock(__instance)) return true;
        __instance.AcceptEvent();
        return false;
    }

    [HarmonyPatch(typeof(NMerchantSlot), nameof(NMerchantSlot._GuiInput))]
    [HarmonyPrefix]
    private static bool BlockReadOnlyMerchantInput(NMerchantSlot __instance, InputEvent inputEvent)
    {
        if (!ReplayUiInteractionPolicy.ShouldBlock(__instance)) return true;
        __instance.AcceptEvent();
        return false;
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRun), new[] { typeof(AbstractRoom), typeof(bool) })]
    [HarmonyPrefix]
    private static bool IsolateRunSave(AbstractRoom? preFinishedRoom, ref Task __result)
    {
        if (!ReplayMod.IsIsolated) return true;
        __result = ReplayMod.Mode == ReplayRuntimeMode.Branch
            ? BranchSaveRouter.SaveRunAsync(preFinishedRoom)
            : Task.CompletedTask;
        return false;
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRun), new[] { typeof(AbstractRoom), typeof(bool) })]
    [HarmonyPrefix]
    private static void CaptureFloorBoundaryBeforeSave(AbstractRoom? preFinishedRoom)
    {
        ReplayMod.Recorder?.CaptureFloorBoundary(preFinishedRoom);
    }

    [HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption))]
    [HarmonyPrefix]
    private static bool BeforeEventOption(int index)
    {
        if (ReplayInputGate.BlockGameplayInput) return false;
        return true;
    }

    [HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.OptionButtonClicked))]
    [HarmonyPrefix]
    private static bool BeforeEventOptionButton(EventOption option, int index)
    {
        if (ReplayInputGate.BlockGameplayInput) return false;
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
        {
            string payload = option.IsProceed ? "proceed" : $"option:{index}";
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.EventOption,
                option.IsProceed ? "Event proceed" : $"Event option {index + 1}", payload);
        }
        return true;
    }

    [HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.ChooseLocalOption))]
    [HarmonyPrefix]
    private static bool BeforeRestSiteOption(int index, ref Task<bool> __result)
    {
        if (ReplayInputGate.BlockGameplayInput)
        {
            __result = Task.FromResult(false);
            return false;
        }
        return true;
    }

    [HarmonyPatch(typeof(NRestSiteButton), "SelectOption")]
    [HarmonyPrefix]
    private static bool BeforeRestSiteButton(RestSiteOption option, ref Task __result)
    {
        if (ReplayInputGate.BlockGameplayInput)
        {
            __result = Task.CompletedTask;
            return false;
        }
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
        {
            int index = NRestSiteRoom.Instance?.Options.ToList().IndexOf(option) ?? -1;
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.RestSiteOption,
                $"Rest site option {index + 1}", index.ToString());
        }
        return true;
    }

    [HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SelectLocalReward))]
    [HarmonyPrefix]
    private static bool BeforeRewardSelected(Reward reward, ref Task<bool> __result)
    {
        if (ReplayInputGate.BlockGameplayInput)
        {
            __result = Task.FromResult(false);
            return false;
        }
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.RewardSelect,
                "Reward selected: " + reward.GetType().Name, $"{reward.RewardsSetIndex}|{reward.GetType().FullName}");
        return true;
    }

    [HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SkipLocalRewardsSet))]
    [HarmonyPrefix]
    private static bool BeforeRewardsSkipped()
    {
        if (ReplayInputGate.BlockGameplayInput) return false;
        return true;
    }

    [HarmonyPatch(typeof(NRewardsScreen), "OnProceedButtonPressed")]
    [HarmonyPrefix]
    private static bool BeforeRewardsProceed()
    {
        if (ReplayInputGate.BlockGameplayInput) return false;
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.RewardsProceed, "Rewards proceed/skip", "");
        return true;
    }

    [HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper))]
    [HarmonyPrefix]
    private static bool BeforeMerchantPurchase(MerchantEntry __instance, MerchantInventory? inventory, ref Task<bool> __result)
    {
        if (ReplayInputGate.BlockGameplayInput)
        {
            __result = Task.FromResult(false);
            return false;
        }
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
        {
            int index = inventory?.AllEntries.ToList().IndexOf(__instance) ?? -1;
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.MerchantPurchase,
                "Merchant purchase: " + __instance.GetType().Name, index.ToString());
        }
        return true;
    }

    [HarmonyPatch(typeof(MerchantCardRemovalEntry), nameof(MerchantCardRemovalEntry.OnTryPurchaseWrapper))]
    [HarmonyPrefix]
    private static bool BeforeMerchantCardRemoval(MerchantInventory? inventory, ref Task<bool> __result)
    {
        if (ReplayInputGate.BlockGameplayInput)
        {
            __result = Task.FromResult(false);
            return false;
        }
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.MerchantCardRemoval, "Merchant card removal", "");
        return true;
    }

    [HarmonyPatch(typeof(NMerchantInventory), nameof(NMerchantInventory.Open))]
    [HarmonyPrefix]
    private static bool BeforeMerchantInventoryOpen()
    {
        if (ReplayInputGate.BlockGameplayInput) return false;
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.MerchantOpen, "Merchant inventory opened", "");
        return true;
    }

    [HarmonyPatch(typeof(NMerchantInventory), "Close")]
    [HarmonyPrefix]
    private static bool BeforeMerchantInventoryClose()
    {
        if (ReplayInputGate.BlockGameplayInput) return false;
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.MerchantClose, "Merchant inventory closed", "");
        return true;
    }

    [HarmonyPatch(typeof(NMerchantRoom), "HideScreen")]
    [HarmonyPrefix]
    private static bool BeforeMerchantProceed()
    {
        if (ReplayInputGate.BlockGameplayInput) return false;
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.MerchantProceed, "Merchant proceed", "");
        return true;
    }

    [HarmonyPatch(typeof(OneOffSynchronizer), nameof(OneOffSynchronizer.DoLocalTreasureRoomRewards))]
    [HarmonyPrefix]
    private static bool BeforeTreasureOpened(ref Task<int> __result)
    {
        if (ReplayInputGate.BlockGameplayInput)
        {
            __result = Task.FromResult(0);
            return false;
        }
        return true;
    }

    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.PickRelicLocally))]
    [HarmonyPrefix]
    private static bool BeforeTreasureRelicPicked(int? index)
    {
        if (ReplayInputGate.BlockGameplayInput) return false;
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal && !_insideTreasureProceed)
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.TreasureRelic,
                index.HasValue ? $"Treasure relic {index.Value + 1}" : "Treasure relic skipped",
                index?.ToString() ?? "skip");
        return true;
    }

    [HarmonyPatch(typeof(NTreasureRoom), "OpenChest")]
    [HarmonyPrefix]
    private static bool BeforeTreasureChest(ref Task __result)
    {
        if (ReplayInputGate.BlockGameplayInput)
        {
            __result = Task.CompletedTask;
            return false;
        }
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.TreasureOpen, "Treasure opened", "");
        return true;
    }

    [HarmonyPatch(typeof(NTreasureRoom), "OnProceedButtonPressed")]
    [HarmonyPrefix]
    private static bool BeforeTreasureProceed()
    {
        if (ReplayInputGate.BlockGameplayInput) return false;
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
        {
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.TreasureProceed, "Treasure proceed/skip", "");
            _insideTreasureProceed = true;
        }
        return true;
    }

    [HarmonyPatch(typeof(NTreasureRoom), "OnProceedButtonPressed")]
    [HarmonyPostfix]
    private static void AfterTreasureProceed()
    {
        _insideTreasureProceed = false;
    }

    [HarmonyPatch(typeof(NTreasureRoom), "OnProceedButtonPressed")]
    [HarmonyFinalizer]
    private static void FinalizeTreasureProceed()
    {
        _insideTreasureProceed = false;
    }

    [HarmonyPatch(typeof(NAncientEventLayout), "OnDialogueHitboxClicked")]
    [HarmonyPrefix]
    private static bool BeforeAncientDialogueAdvance()
    {
        if (ReplayInputGate.BlockGameplayInput) return false;
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.AncientDialogue, "Ancient dialogue advance", "");
        return true;
    }

    [HarmonyPatch(typeof(NFakeMerchant), "HideScreen")]
    [HarmonyPrefix]
    private static bool BeforeFakeMerchantProceed()
    {
        if (ReplayInputGate.BlockGameplayInput) return false;
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.FakeMerchantProceed, "Event merchant proceed", "");
        return true;
    }

    [HarmonyPatch(typeof(NCrystalSphereScreen), "SetBigDivination")]
    [HarmonyPrefix]
    private static bool BeforeCrystalBigTool() => RecordCrystalTool("big");

    [HarmonyPatch(typeof(NCrystalSphereScreen), "SetSmallDivination")]
    [HarmonyPrefix]
    private static bool BeforeCrystalSmallTool() => RecordCrystalTool("small");

    private static bool RecordCrystalTool(string tool)
    {
        if (ReplayInputGate.BlockGameplayInput) return false;
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.CrystalTool, $"Crystal sphere {tool} tool", tool);
        return true;
    }

    [HarmonyPatch(typeof(NCrystalSphereScreen), "OnCellClicked")]
    [HarmonyPrefix]
    private static bool BeforeCrystalCell(NCrystalSphereCell cell, ref Task __result)
    {
        if (ReplayInputGate.BlockGameplayInput)
        {
            __result = Task.CompletedTask;
            return false;
        }
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.CrystalCell,
                $"Crystal sphere cell {cell.Entity.X},{cell.Entity.Y}", $"{cell.Entity.X},{cell.Entity.Y}");
        return true;
    }

    [HarmonyPatch(typeof(NCrystalSphereScreen), "OnProceedButtonPressed")]
    [HarmonyPrefix]
    private static bool BeforeCrystalProceed()
    {
        if (ReplayInputGate.BlockGameplayInput) return false;
        if (ReplayMod.Mode == ReplayRuntimeMode.Normal)
            ReplayMod.Recorder?.RecordExternalOperation(RunReplayInputKinds.CrystalProceed, "Crystal sphere proceed", "");
        return true;
    }

    [HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueue), new[] { typeof(GameAction) })]
    [HarmonyPrefix]
    private static bool BlockPlaybackActionInput() => !ReplayInputGate.BlockGameplayInput;

    [HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueueHookAction))]
    [HarmonyPrefix]
    private static bool BlockPlaybackHookInput() => !ReplayInputGate.BlockGameplayInput;

    [HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestResumeActionAfterPlayerChoice))]
    [HarmonyPrefix]
    private static bool BlockPlaybackResumeInput() => !ReplayInputGate.BlockGameplayInput;

    [HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice))]
    [HarmonyPrefix]
    private static bool BlockPlaybackChoiceInput() => !ReplayInputGate.BlockGameplayInput;

    [HarmonyPatch(typeof(MapSelectionSynchronizer), nameof(MapSelectionSynchronizer.PlayerVotedForMapCoord))]
    [HarmonyPrefix]
    private static bool BlockPlaybackMapTravel() => !ReplayInputGate.BlockGameplayInput;

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.IncrementNumReloads))]
    [HarmonyPrefix]
    private static bool IsolateReloadIncrement(ref Task __result)
    {
        if (!ReplayMod.IsIsolated) return true;
        __result = Task.CompletedTask;
        return false;
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveProgressFile))]
    [HarmonyPrefix]
    private static bool IsolateProgressSave() => !ReplayMod.IsIsolated;

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.UpdateProgressWithRunData))]
    [HarmonyPrefix]
    private static bool IsolateRunProgress() => !ReplayMod.IsIsolated;

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.UpdateProgressAfterCombatWon))]
    [HarmonyPrefix]
    private static bool IsolateCombatProgress() => !ReplayMod.IsIsolated;

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRunHistory))]
    [HarmonyPrefix]
    private static bool IsolateRunHistory() => !ReplayMod.IsIsolated;

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.DeleteCurrentRun))]
    [HarmonyPrefix]
    private static bool IsolateCurrentRunDelete() => !ReplayMod.IsIsolated;

    [HarmonyPatch(typeof(AchievementsHelper), nameof(AchievementsHelper.AfterRunEnded))]
    [HarmonyPrefix]
    private static bool IsolateRunEndAchievements() => !ReplayMod.IsIsolated;

    [HarmonyPatch(typeof(AchievementsHelper), nameof(AchievementsHelper.CheckForDefeatedAllEnemiesAchievement))]
    [HarmonyPrefix]
    private static bool IsolateCombatAchievements() => !ReplayMod.IsIsolated;

    [HarmonyPatch(typeof(AchievementsHelper), nameof(AchievementsHelper.AfterBossDefeated))]
    [HarmonyPrefix]
    private static bool IsolateBossAchievements() => !ReplayMod.IsIsolated;

    [HarmonyPatch(typeof(AchievementsUtil), nameof(AchievementsUtil.Unlock))]
    [HarmonyPrefix]
    private static bool IsolateAllAchievementUnlocks() => !ReplayMod.IsIsolated;

    [HarmonyPatch(typeof(MetricUtilities), nameof(MetricUtilities.UploadRunMetrics), new[] { typeof(SerializableRun), typeof(bool), typeof(ulong) })]
    [HarmonyPrefix]
    private static bool IsolateRunMetrics() => !ReplayMod.IsIsolated;

    [HarmonyPatch(typeof(MetricUtilities), nameof(MetricUtilities.UploadAchievementMetric))]
    [HarmonyPrefix]
    private static bool IsolateAchievementMetrics() => !ReplayMod.IsIsolated;

    [HarmonyPatch(typeof(MetricUtilities), nameof(MetricUtilities.UploadEpochMetric))]
    [HarmonyPrefix]
    private static bool IsolateEpochMetrics() => !ReplayMod.IsIsolated;

    [HarmonyPatch(typeof(LeaderboardManager), nameof(LeaderboardManager.UploadLocalScore))]
    [HarmonyPrefix]
    private static bool IsolateLeaderboardUpload(ref Task __result)
    {
        if (!ReplayMod.IsIsolated) return true;
        __result = Task.CompletedTask;
        return false;
    }
}
