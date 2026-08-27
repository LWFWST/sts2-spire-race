using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2SpireRace.Replay;

public static class BranchSaveRouter
{
    private static ActionExecutor? _executor;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        CombatManager.Instance.CombatSetUp += _ => SetCombatState(true);
        CombatManager.Instance.CombatEnded += _ => SetCombatState(false);
    }

    public static void AttachRunHooks()
    {
        if (_executor != null) _executor.AfterActionExecuted -= OnActionExecuted;
        _executor = RunManager.Instance.ActionExecutor;
        if (_executor != null) _executor.AfterActionExecuted += OnActionExecuted;
    }

    public static async Task SaveRunAsync(AbstractRoom? preFinishedRoom)
    {
        ReplayBranchManifest? branch = ReplayMod.ActiveBranch;
        if (ReplayMod.Mode != ReplayRuntimeMode.Branch || branch == null || !RunManager.Instance.IsInProgress) return;
        try
        {
            string dir = ReplayMod.Storage.GetBranchDirectory(branch.BranchId);
            string path = Path.Combine(dir, branch.CurrentRunFile);
            SerializableRun save = RunManager.Instance.ToSave(preFinishedRoom);
            using MemoryStream stream = new();
            await JsonSerializer.SerializeAsync(stream, save, JsonSerializationUtility.GetTypeInfo<SerializableRun>());
            string temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, stream.ToArray());
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            branch.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ReplayMod.Storage.SaveBranchManifest(branch);
            UpdateCatalog(branch);
        }
        catch (Exception ex)
        {
            Log.Error($"[SpireRaceReplay] Failed to save practice branch: {ex}");
        }
    }

    public static void FlushActiveCombat()
    {
        ReplayBranchManifest? branch = ReplayMod.ActiveBranch;
        if (ReplayMod.Mode != ReplayRuntimeMode.Branch || branch == null) return;
        try
        {
            string path = Path.Combine(ReplayMod.Storage.GetBranchDirectory(branch.BranchId), branch.ActiveCombatFile);
            NativeReplayAdapter.FlushCurrent(path);
            branch.ActiveEventCount = NativeReplayAdapter.GetEventCount();
            branch.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ReplayMod.Storage.SaveBranchManifest(branch);
        }
        catch (Exception ex)
        {
            Log.Error($"[SpireRaceReplay] Failed to flush branch combat: {ex}");
        }
    }

    public static ReplayBranchManifest CreateBranch(CombatReplayManifest source, int markerIndex)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ReplayBranchManifest branch = new()
        {
            BranchId = $"branch_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}",
            Name = $"{source.Encounter} floor {source.Floor} @ state {markerIndex}",
            SourceRunId = source.RunId,
            SourceCombatId = source.CombatId,
            SourceMarker = markerIndex,
            CreatedAtUnixMs = now,
            UpdatedAtUnixMs = now
        };
        ReplayMod.Storage.SaveBranchManifest(branch);
        UpdateCatalog(branch);
        return branch;
    }

    public static ReplayBranchManifest CreateBranch(RunReplayManifest source, RunReplayMarker checkpoint)
    {
        if (string.IsNullOrEmpty(checkpoint.CheckpointFile))
            throw new InvalidDataException("The selected floor checkpoint has no save data.");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ReplayBranchManifest branch = new()
        {
            BranchId = $"branch_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}",
            Name = $"{source.Character} floor {checkpoint.Floor} practice",
            SourceRunId = source.RunId,
            SourceCombatId = "",
            SourceMarker = checkpoint.Index,
            CreatedAtUnixMs = now,
            UpdatedAtUnixMs = now,
            InCombat = false
        };
        string branchDir = ReplayMod.Storage.GetBranchDirectory(branch.BranchId);
        string sourceSave = ReplayMod.Storage.ResolveRelativePath(checkpoint.CheckpointFile);
        File.Copy(sourceSave, Path.Combine(branchDir, branch.CurrentRunFile), overwrite: true);
        ReplayMod.Storage.SaveBranchManifest(branch);
        UpdateCatalog(branch);
        return branch;
    }

    private static void OnActionExecuted(GameAction _)
    {
        if (ReplayMod.Mode == ReplayRuntimeMode.Branch) FlushActiveCombat();
    }

    private static void SetCombatState(bool inCombat)
    {
        ReplayBranchManifest? branch = ReplayMod.ActiveBranch;
        if (ReplayMod.Mode != ReplayRuntimeMode.Branch || branch == null) return;
        branch.InCombat = inCombat;
        branch.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ReplayMod.Storage.SaveBranchManifest(branch);
        UpdateCatalog(branch);
        if (inCombat) FlushActiveCombat();
        else _ = SaveRunAsync(RunManager.Instance.DebugOnlyGetState()?.CurrentRoom);
    }

    private static void UpdateCatalog(ReplayBranchManifest branch)
    {
        ReplayCatalog catalog = ReplayMod.Storage.LoadCatalog();
        int index = catalog.Branches.FindIndex(b => b.BranchId == branch.BranchId);
        if (index >= 0) catalog.Branches[index] = branch;
        else catalog.Branches.Add(branch);
        ReplayMod.Storage.SaveCatalog(catalog);
    }
}
