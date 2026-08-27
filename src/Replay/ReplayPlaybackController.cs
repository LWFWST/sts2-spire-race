using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2SpireRace.Replay;

public sealed class ReplayPlaybackController
{
    private readonly ReplayStorage _storage;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private CombatReplayManifest? _manifest;
    private CombatReplay? _replay;
    private ReplayTimeline? _timeline;
    private int _nextEventIndex;
    private int _currentMarkerIndex;
    private bool _playing;
    private bool _pauseRequested;
    private ReplayControlsOverlay? _controls;

    public int CurrentMarkerIndex => _currentMarkerIndex;
    public int MarkerCount => _timeline?.Markers.Count ?? 0;
    public bool IsPlaying => _playing;

    public ReplayPlaybackController(ReplayStorage storage) => _storage = storage;

    public async Task StartAsync(CombatReplayManifest manifest, int markerIndex = 0, bool branch = false, bool showControls = true)
    {
        await _operationLock.WaitAsync();
        try
        {
            if (!CompatibilityService.IsCompatible(manifest.Compatibility, out string reason))
                throw new InvalidOperationException("Replay is locked: " + reason);
            _manifest = manifest;
            _replay = NativeReplayAdapter.ReadReplay(_storage.ResolveRelativePath(manifest.ReplayFile));
            _timeline = _storage.LoadTimeline(_storage.ResolveRelativePath(manifest.TimelineFile));
            if (_timeline.Markers.Count == 0) throw new InvalidDataException("Replay has no stable markers.");
            markerIndex = Math.Clamp(markerIndex, 0, _timeline.Markers.Count - 1);
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            ReplayMod.Mode = branch ? ReplayRuntimeMode.Branch : ReplayRuntimeMode.Playback;
            await BuildRunAsync(_replay, branch);
            await AdvanceToMarkerCoreAsync(markerIndex, fast: markerIndex > 0);
            if (branch) CompleteTakeover();
            else if (showControls) ShowControls();
        }
        catch
        {
            Engine.TimeScale = 1.0;
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            throw;
        }
        finally { _operationLock.Release(); }
    }

    public async Task PlayAsync()
    {
        if (_timeline == null || _playing) return;
        _playing = true;
        _pauseRequested = false;
        UpdateControls();
        try
        {
            while (!_pauseRequested && _currentMarkerIndex < _timeline.Markers.Count - 1)
                await StepCoreAsync();
        }
        finally
        {
            _playing = false;
            UpdateControls();
        }
    }

    public void Pause() => _pauseRequested = true;

    public async Task StepAsync()
    {
        if (_playing) return;
        await _operationLock.WaitAsync();
        try { await StepCoreAsync(); }
        finally { _operationLock.Release(); }
    }

    public async Task SeekAsync(int markerIndex)
    {
        if (_manifest == null || _timeline == null) return;
        markerIndex = Math.Clamp(markerIndex, 0, _timeline.Markers.Count - 1);
        await _operationLock.WaitAsync();
        try
        {
            _pauseRequested = true;
            CombatReplayManifest manifest = _manifest;
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            ReplayMod.Mode = ReplayRuntimeMode.Playback;
            _replay = NativeReplayAdapter.ReadReplay(_storage.ResolveRelativePath(manifest.ReplayFile));
            _timeline = _storage.LoadTimeline(_storage.ResolveRelativePath(manifest.TimelineFile));
            await BuildRunAsync(_replay, branch: false);
            await AdvanceToMarkerCoreAsync(markerIndex, fast: markerIndex > 0);
            ShowControls();
        }
        finally { _operationLock.Release(); }
    }

    public async Task TakeOverAsync()
    {
        if (_manifest == null || _timeline == null || _replay == null) return;
        await _operationLock.WaitAsync();
        try
        {
            ReplayMarker marker = _timeline.Markers[_currentMarkerIndex];
            if (NativeReplayAdapter.CalculateCurrentStateHash() != marker.StateHash)
                throw new InvalidOperationException("Current state diverged from the recording; takeover is disabled.");
            ReplayBranchManifest branch = BranchSaveRouter.CreateBranch(_manifest, _currentMarkerIndex);
            CombatReplay source = _replay;
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            ReplayMod.Mode = ReplayRuntimeMode.Branch;
            ReplayMod.ActiveBranch = branch;
            await BuildRunAsync(source, branch: true);
            await AdvanceToMarkerCoreAsync(_currentMarkerIndex, fast: true);
            CompleteTakeover();
        }
        finally { _operationLock.Release(); }
    }

    public async Task ResumeBranchAsync(ReplayBranchManifest branch)
    {
        await _operationLock.WaitAsync();
        try
        {
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            ReplayMod.Mode = ReplayRuntimeMode.Branch;
            ReplayMod.ActiveBranch = branch;
            string dir = _storage.GetBranchDirectory(branch.BranchId);
            string activeCombat = Path.Combine(dir, branch.ActiveCombatFile);
            if (branch.InCombat && File.Exists(activeCombat))
            {
                CombatReplay replay = NativeReplayAdapter.ReadReplay(activeCombat);
                await BuildRunAsync(replay, branch: true);
                CombatManager.Instance.Unpause();
                while (_nextEventIndex < replay.events.Count)
                    await DeliverEventAsync(replay.events[_nextEventIndex++]);
                await WaitForActionQueueAsync();
                CompleteTakeover();
                return;
            }

            string currentRun = Path.Combine(dir, branch.CurrentRunFile);
            if (!File.Exists(currentRun))
                throw new FileNotFoundException("This branch has no autosave yet.", currentRun);
            SerializableRun save = JsonSerializer.Deserialize(
                File.ReadAllText(currentRun),
                MegaCrit.Sts2.Core.Saves.JsonSerializationUtility.GetTypeInfo<SerializableRun>())
                ?? throw new InvalidDataException("Branch save was empty.");
            RunState state = RunState.FromSerializable(save);
            await RunManager.Instance.SetUpSavedSingleplayer(state, save);
            RuntimeIsolation.Apply();
            await NGame.Instance!.LoadRun(state, save.PreFinishedRoom);
            BranchSaveRouter.AttachRunHooks();
            BranchStatusOverlay.Show(branch.Name);
        }
        catch
        {
            Engine.TimeScale = 1.0;
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            throw;
        }
        finally { _operationLock.Release(); }
    }

    public void SetSpeed(double speed)
    {
        Engine.TimeScale = Math.Clamp(speed, 0.5, 4.0);
        UpdateControls();
    }

    public void ExitToMainMenu()
    {
        _pauseRequested = true;
        Engine.TimeScale = 1.0;
        _controls?.QueueFree();
        _controls = null;
        ReplayMod.ResetRuntimeMode();
        if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(NMainMenu.Create(openTimeline: false));
    }

    private async Task BuildRunAsync(CombatReplay replay, bool branch)
    {
        RunState runState = RunState.FromSerializable(replay.serializableRun);
        ulong netId = runState.Players[0].NetId;
        if (branch) await RunManager.Instance.SetUpSavedSingleplayer(runState, replay.serializableRun);
        else ReplayVersionCompat.SetUpReplay(runState, replay, netId);
        RuntimeIsolation.Apply();
        RunManager.Instance.CombatStateSynchronizer.IsDisabled = true;
        await PreloadManager.LoadRunAssets(runState.Players.Select(p => p.Character));
        await PreloadManager.LoadActAssets(runState.Act);
        RunManager.Instance.Launch();
        NAudioManager.Instance?.StopMusic();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(runState));
        await RunManager.Instance.GenerateMap();
        RunManager.Instance.ActionQueueSet.FastForwardNextActionId(replay.nextActionId);
        RunManager.Instance.ActionQueueSynchronizer.FastForwardHookId(replay.nextHookId);
        if (!branch) RunManager.Instance.ChecksumTracker.LoadReplayChecksums(replay.checksumData, replay.nextChecksumId);
        RunManager.Instance.PlayerChoiceSynchronizer.FastForwardChoiceIds(replay.choiceIds);
        RunManager.Instance.RewardsSetSynchronizer.FastForwardRewardIds(replay.rewardIds);
        await RunManager.Instance.LoadIntoLatestMapCoord(AbstractRoom.FromSerializable(replay.serializableRun.PreFinishedRoom, runState));
        SceneTree tree = NGame.Instance!.GetTree();
        while (!CombatManager.Instance.IsInProgress) await tree.Root.AwaitProcessFrame();
        _nextEventIndex = 0;
        _currentMarkerIndex = 0;
        CombatManager.Instance.Pause();
    }

    private async Task StepCoreAsync()
    {
        if (_timeline == null || _currentMarkerIndex >= _timeline.Markers.Count - 1) return;
        await AdvanceToMarkerCoreAsync(_currentMarkerIndex + 1, fast: false);
    }

    private async Task AdvanceToMarkerCoreAsync(int targetIndex, bool fast)
    {
        if (_timeline == null || _replay == null) return;
        ReplayMarker target = _timeline.Markers[targetIndex];
        double oldScale = Engine.TimeScale;
        if (fast) Engine.TimeScale = 4.0;
        CombatManager.Instance.Unpause();
        try
        {
            while (_nextEventIndex < target.EventCount && _nextEventIndex < _replay.events.Count)
                await DeliverEventAsync(_replay.events[_nextEventIndex++]);
            await WaitForStateAsync(target);
            CombatManager.Instance.Pause();
            _currentMarkerIndex = targetIndex;
            UpdateControls();
        }
        finally
        {
            if (fast) Engine.TimeScale = oldScale;
        }
    }

    private static async Task DeliverEventAsync(CombatReplayEvent replayEvent)
    {
        SceneTree tree = NGame.Instance!.GetTree();
        RunState state = RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("Replay run disappeared.");
        switch (replayEvent.eventType)
        {
            case CombatReplayEventType.GameAction:
            {
                while (CombatManager.Instance.EndingPlayerTurnPhaseOne || CombatManager.Instance.EndingPlayerTurnPhaseTwo)
                    await tree.Root.AwaitProcessFrame();
                Player player = state.GetPlayer(replayEvent.playerId!.Value) ?? throw new InvalidOperationException("Replay action owner not found.");
                GameAction action = replayEvent.action!.ToGameAction(player);
                if (action.ActionType == GameActionType.CombatPlayPhaseOnly)
                {
                    while (CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Enemy)
                        await tree.Root.AwaitProcessFrame();
                }
                RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(action);
                if (action is EndPlayerTurnAction or ReadyToBeginEnemyTurnAction)
                    await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                break;
            }
            case CombatReplayEventType.HookAction:
                RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(
                    RunManager.Instance.ActionQueueSynchronizer.GetHookActionForId(
                        replayEvent.hookId!.Value, replayEvent.playerId!.Value, replayEvent.gameActionType!.Value));
                break;
            case CombatReplayEventType.ResumeAction:
                RunManager.Instance.ActionQueueSet.ResumeActionWithoutSynchronizing(replayEvent.actionId!.Value);
                break;
            case CombatReplayEventType.PlayerChoice:
            {
                Player player = state.GetPlayer(replayEvent.playerId!.Value) ?? throw new InvalidOperationException("Replay choice owner not found.");
                RunManager.Instance.PlayerChoiceSynchronizer.ReceiveReplayChoice(
                    player, replayEvent.choiceId!.Value, replayEvent.playerChoiceResult!.Value);
                break;
            }
            default:
                throw new InvalidEnumArgumentException();
        }
    }

    private static async Task WaitForStateAsync(ReplayMarker marker)
    {
        SceneTree tree = NGame.Instance!.GetTree();
        ulong deadline = Time.GetTicksMsec() + 30000;
        while (Time.GetTicksMsec() < deadline)
        {
            if (NativeReplayAdapter.CalculateCurrentStateHash() == marker.StateHash) return;
            await tree.Root.AwaitProcessFrame();
        }
        throw new InvalidOperationException($"Replay diverged or timed out at marker {marker.Index} ({marker.Label}).");
    }

    private static async Task WaitForActionQueueAsync()
    {
        SceneTree tree = NGame.Instance!.GetTree();
        ulong deadline = Time.GetTicksMsec() + 30000;
        while (Time.GetTicksMsec() < deadline)
        {
            ActionExecutor executor = RunManager.Instance.ActionExecutor;
            if (!executor.IsRunning && executor.CurrentlyRunningAction == null) return;
            await tree.Root.AwaitProcessFrame();
        }
        throw new TimeoutException("Timed out restoring the branch combat action stream.");
    }

    private void CompleteTakeover()
    {
        NativeReplayAdapter.DisableReplayChecksumComparison();
        RuntimeIsolation.Apply();
        Engine.TimeScale = 1.0;
        CombatManager.Instance.Unpause();
        _controls?.QueueFree();
        _controls = null;
        BranchSaveRouter.AttachRunHooks();
        BranchSaveRouter.FlushActiveCombat();
        BranchStatusOverlay.Show(ReplayMod.ActiveBranch?.Name ?? "Practice branch");
    }

    private void ShowControls()
    {
        _controls?.QueueFree();
        _controls = ReplayControlsOverlay.Create(this);
        NGame.Instance!.AddChild(_controls);
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (_controls == null || _timeline == null) return;
        ReplayMarker marker = _timeline.Markers[Math.Clamp(_currentMarkerIndex, 0, _timeline.Markers.Count - 1)];
        _controls.UpdateState(_playing, _currentMarkerIndex, _timeline.Markers.Count, marker.Label, Engine.TimeScale);
    }
}
