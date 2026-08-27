using System;
using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.TreasureRooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2SpireRace.Replay;

public sealed class RunReplayPlaybackController
{
    private readonly ReplayStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RunReplayManifest? _run;
    private RunReplayTimeline? _timeline;
    private RunReplayInputStream? _inputs;
    private RunReplayControlsOverlay? _controls;
    private bool _playing;
    private bool _pauseRequested;
    private int _nextEventIndex;
    private int _checkpointIndex;
    private double _speed = 1.0;
    private long _displayElapsedMs;
    private Exception? _backgroundUiFailure;
    private int _pendingLocalCardRewardChoices;
    private int _pendingUiTasks;
    private int _sessionGeneration;
    private Func<CancellationToken, Task<RunReplayManifest?>>? _liveRefresh;
    private CancellationTokenSource? _liveLifetime;

    public bool IsPlaying => _playing;
    public int MarkerIndex => _checkpointIndex;
    public int MarkerCount => _timeline?.Markers.Count ?? 0;
    public long DurationMs => Math.Max(
        _inputs?.Events.LastOrDefault()?.ElapsedMs ?? 0,
        _timeline?.Markers.LastOrDefault()?.ElapsedMs ?? 0);
    public long CurrentElapsedMs => _displayElapsedMs;
    public int EventIndex => _nextEventIndex;
    public int EventCount => _inputs?.Events.Count ?? 0;
    public string CurrentLabel => _nextEventIndex <= 0
        ? (_timeline?.Markers.ElementAtOrDefault(_checkpointIndex)?.Label ?? "Ready")
        : (_inputs?.Events.ElementAtOrDefault(_nextEventIndex - 1)?.Label ?? "Ready");
    public bool CanTakeOver => !_playing && CurrentCheckpointIsExact();
    public RunReplayManifest? CurrentRun => _run;
    public long DisplayRaceElapsedMs
    {
        get
        {
            if (_run == null || _liveRefresh == null)
                return _displayElapsedMs;
            var liveElapsed = _run.RaceElapsedMs;
            if (!_run.RaceTimerPaused && _run.RaceElapsedUpdatedAtUnixMs > 0)
                liveElapsed += Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _run.RaceElapsedUpdatedAtUnixMs);
            return Math.Max(_displayElapsedMs, liveElapsed);
        }
    }

    public RunReplayPlaybackController(ReplayStorage storage, ReplayPlaybackController _)
    {
        _storage = storage;
    }

    public async Task StartAsync(RunReplayManifest run, int initialMarkerIndex = 0)
    {
        if (!CompatibilityService.IsCompatible(run.Compatibility, out string reason))
            throw new InvalidOperationException("Run replay is locked: " + reason);
        if (run.Compatibility.FormatVersion < 5 || string.IsNullOrEmpty(run.InputFile))
            throw new InvalidDataException("This recording predates operation-driven whole-run replay.");
        if (!_storage.ValidateRunData(run, out string dataReason))
            throw new InvalidDataException("Run replay is not playable: " + dataReason);
        _run = run;
        _timeline = _storage.LoadRunTimeline(_storage.ResolveRelativePath(run.TimelineFile));
        _inputs = _storage.LoadInputStream(_storage.ResolveRelativePath(run.InputFile));
        if (_timeline.Markers.Count == 0) throw new InvalidDataException("Run replay has no floor checkpoints.");
        await SeekAsync(Math.Clamp(initialMarkerIndex, 0, _timeline.Markers.Count - 1));
        ShowControls();
    }

    public void EnableLiveRefresh(Func<CancellationToken, Task<RunReplayManifest?>> refresh)
    {
        _liveLifetime?.Cancel();
        _liveLifetime = new CancellationTokenSource();
        _liveRefresh = refresh;
    }

    public async Task PlayAsync()
    {
        if (_inputs == null || _playing) return;
        _playing = true;
        _pauseRequested = false;
        ResumeRuntime();
        UpdateControls();
        try
        {
            while (true)
            {
                while (_nextEventIndex < _inputs.Events.Count)
                {
                    RunReplayInputEvent input = _inputs.Events[_nextEventIndex];
                    long priorTime = _nextEventIndex == 0 ? CurrentCheckpoint.ElapsedMs : _inputs.Events[_nextEventIndex - 1].ElapsedMs;
                    await DelayRecordedIntervalAsync(Math.Max(0, input.ElapsedMs - priorTime));
                    await DeliverInputAsync(input);
                    _nextEventIndex++;
                    _displayElapsedMs = input.ElapsedMs;
                    UpdateCheckpointIndex();
                    UpdateControls();
                    bool groupEnded = _nextEventIndex >= _inputs.Events.Count ||
                        _inputs.Events[_nextEventIndex].Operation != input.Operation;
                    if (groupEnded)
                    {
                        await WaitForStableAsync();
                        if (_pauseRequested)
                        {
                            PauseRuntime();
                            return;
                        }
                    }
                }
                await WaitForStableAsync();
                if (_liveRefresh is null || _run?.Outcome != "IN_PROGRESS" || _liveLifetime?.IsCancellationRequested == true)
                {
                    PauseRuntime();
                    break;
                }
                PauseRuntime();
                var refresh = _liveRefresh;
                var liveToken = _liveLifetime?.Token ?? CancellationToken.None;
                RunReplayManifest? updated;
                try { updated = await refresh(liveToken); }
                catch (OperationCanceledException) { break; }
                if (updated is null)
                {
                    await Task.Delay(250, liveToken);
                    continue;
                }
                _run = updated;
                _timeline = _storage.LoadRunTimeline(_storage.ResolveRelativePath(updated.TimelineFile));
                _inputs = _storage.LoadInputStream(_storage.ResolveRelativePath(updated.InputFile));
                if (_nextEventIndex < _inputs.Events.Count) ResumeRuntime();
                else await Task.Delay(250, liveToken);
            }
        }
        finally
        {
            _playing = false;
            UpdateControls();
        }
    }

    public void Pause()
    {
        _pauseRequested = true;
        UpdateControls();
    }

    public async Task StepAsync()
    {
        if (_inputs == null || _playing || _nextEventIndex >= _inputs.Events.Count) return;
        await _gate.WaitAsync();
        try
        {
            ResumeRuntime();
            int operation = _inputs.Events[_nextEventIndex].Operation;
            do
            {
                await DeliverInputAsync(_inputs.Events[_nextEventIndex]);
                _displayElapsedMs = _inputs.Events[_nextEventIndex].ElapsedMs;
                _nextEventIndex++;
            }
            while (_nextEventIndex < _inputs.Events.Count && _inputs.Events[_nextEventIndex].Operation == operation);
            await WaitForStableAsync();
            PauseRuntime();
            UpdateCheckpointIndex();
            UpdateControls();
        }
        finally { _gate.Release(); }
    }

    public Task SeekTimeAsync(long elapsedMs)
    {
        if (_timeline == null) return Task.CompletedTask;
        _pauseRequested = true;
        int index = _timeline.Markers
            .Select((marker, index) => (marker, index))
            .OrderBy(pair => Math.Abs(pair.marker.ElapsedMs - elapsedMs))
            .First().index;
        return SeekAsync(index);
    }

    public async Task SeekAsync(int checkpointIndex)
    {
        if (_timeline == null || _run == null) return;
        checkpointIndex = Math.Clamp(checkpointIndex, 0, _timeline.Markers.Count - 1);
        _pauseRequested = true;
        while (_playing)
            await NGame.Instance!.GetTree().Root.AwaitProcessFrame();
        await _gate.WaitAsync();
        try
        {
            await LoadCheckpointAsync(checkpointIndex, branch: false);
            PauseRuntime();
            UpdateControls();
        }
        finally { _gate.Release(); }
    }

    public async Task TakeOverAsync()
    {
        if (_run == null || _timeline == null || !CanTakeOver)
            throw new InvalidOperationException("A practice run can only start from an exact floor checkpoint.");
        await _gate.WaitAsync();
        try
        {
            RunReplayMarker marker = CurrentCheckpoint;
            ReplayBranchManifest branch = BranchSaveRouter.CreateBranch(_run, marker);
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            ReplayMod.Mode = ReplayRuntimeMode.Branch;
            ReplayMod.ActiveBranch = branch;
            SerializableRun save = _storage.LoadRunCheckpoint(_storage.ResolveRelativePath(marker.CheckpointFile!));
            RunState state = RunState.FromSerializable(save);
            await RunManager.Instance.SetUpSavedSingleplayer(state, save);
            RuntimeIsolation.Apply();
            await NGame.Instance!.LoadRun(state, save.PreFinishedRoom);
            Engine.TimeScale = 1.0;
            _controls?.QueueFree();
            _controls = null;
            BranchSaveRouter.AttachRunHooks();
            await BranchSaveRouter.SaveRunAsync(save.PreFinishedRoom == null ? null : AbstractRoom.FromSerializable(save.PreFinishedRoom, state));
            BranchStatusOverlay.Show(branch.Name);
        }
        finally { _gate.Release(); }
    }

    public void SetSpeed(double speed)
    {
        _speed = Math.Clamp(speed, 0.5, 4.0);
        Engine.TimeScale = _speed;
        UpdateControls();
    }

    public void Exit()
    {
        _liveLifetime?.Cancel();
        _liveRefresh = null;
        _pauseRequested = true;
        Engine.TimeScale = 1.0;
        _controls?.QueueFree();
        _controls = null;
        ReplayMod.ResetRuntimeMode();
        if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu.Create(openTimeline: false));
    }

    private RunReplayMarker CurrentCheckpoint => _timeline!.Markers[Math.Clamp(_checkpointIndex, 0, _timeline.Markers.Count - 1)];

    private bool CurrentCheckpointIsExact()
    {
        return _timeline != null && _timeline.Markers.Any(m => m.EventIndex == _nextEventIndex);
    }

    private async Task LoadCheckpointAsync(int checkpointIndex, bool branch)
    {
        RunReplayMarker marker = _timeline!.Markers[checkpointIndex];
        _sessionGeneration++;
        _pendingLocalCardRewardChoices = 0;
        _pendingUiTasks = 0;
        _backgroundUiFailure = null;
        if (string.IsNullOrEmpty(marker.CheckpointFile))
            throw new InvalidDataException($"Floor checkpoint {marker.Index} has no save data.");
        if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
        ReplayMod.Mode = branch ? ReplayRuntimeMode.Branch : ReplayRuntimeMode.Playback;
        SerializableRun save = _storage.LoadRunCheckpoint(_storage.ResolveRelativePath(marker.CheckpointFile));
        if (!ReplayStorage.HasCompleteCurrentMap(save))
            throw new InvalidDataException($"Floor checkpoint {marker.Index} does not contain the original complete map.");
        RunState state = RunState.FromSerializable(save);
        if (branch)
        {
            await RunManager.Instance.SetUpSavedSingleplayer(state, save);
            RuntimeIsolation.Apply();
            await NGame.Instance!.LoadRun(state, save.PreFinishedRoom);
        }
        else
        {
            CombatReplay shell = NativeReplayAdapter.CreateReplayShell(save, marker);
            ReplayVersionCompat.SetUpReplay(state, shell, state.Players[0].NetId);
            RuntimeIsolation.Apply();
            RunManager.Instance.CombatStateSynchronizer.IsDisabled = true;
            await PreloadManager.LoadRunAssets(state.Players.Select(p => p.Character));
            await PreloadManager.LoadActAssets(state.Act);
            RunManager.Instance.Launch();
            NAudioManager.Instance?.StopMusic();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(state));
            await RunManager.Instance.GenerateMap();
            RunManager.Instance.ActionQueueSet.FastForwardNextActionId(marker.NextActionId);
            RunManager.Instance.ActionQueueSynchronizer.FastForwardHookId(marker.NextHookId);
            RunManager.Instance.PlayerChoiceSynchronizer.FastForwardChoiceIds(marker.ChoiceIds);
            RunManager.Instance.RewardsSetSynchronizer.FastForwardRewardIds(marker.RewardIds);
            NativeReplayAdapter.DisableReplayChecksumComparison();
            await RunManager.Instance.LoadIntoLatestMapCoord(AbstractRoom.FromSerializable(save.PreFinishedRoom, state));
            if (RunManager.Instance.MapDrawingsToLoad != null)
            {
                NRun.Instance!.GlobalUi.MapScreen.Drawings.LoadDrawings(RunManager.Instance.MapDrawingsToLoad);
                RunManager.Instance.MapDrawingsToLoad = null;
            }
        }
        _checkpointIndex = checkpointIndex;
        _nextEventIndex = marker.EventIndex;
        _displayElapsedMs = marker.ElapsedMs;
        Engine.TimeScale = _speed;
    }

    private async Task DeliverInputAsync(RunReplayInputEvent input)
    {
        if (_backgroundUiFailure != null)
            throw new InvalidOperationException("A replay UI operation failed.", _backgroundUiFailure);
        using IDisposable injection = ReplayInputGate.BeginInjection();
        if (input.Kind == RunReplayInputKinds.Native)
        {
            await DeliverNativeEventAsync(NativeReplayAdapter.DeserializeEvent(input.Payload));
            return;
        }
        switch (input.Kind)
        {
            case RunReplayInputKinds.EventOption:
                if (input.Payload == "proceed")
                {
                    await WaitUntilReadyAsync(() => NEventRoom.Instance != null, "event proceed button");
                    StartUiTask(NEventRoom.Proceed(), input.Label);
                }
                else
                {
                    int index = int.Parse(input.Payload.StartsWith("option:", StringComparison.Ordinal)
                        ? input.Payload[7..]
                        : input.Payload);
                    await WaitUntilReadyAsync(
                        () => NEventRoom.Instance != null && RunManager.Instance.EventSynchronizer.Events.Count > 0 &&
                            RunManager.Instance.EventSynchronizer.GetLocalEvent().CurrentOptions.Count > index,
                        "recorded event option");
                    var eventModel = RunManager.Instance.EventSynchronizer.GetLocalEvent();
                    NEventRoom.Instance!.OptionButtonClicked(eventModel.CurrentOptions[index], index);
                }
                break;
            case RunReplayInputKinds.RestSiteOption:
            {
                int index = int.Parse(input.Payload);
                await WaitUntilReadyAsync(
                    () => NRestSiteRoom.Instance != null && NRestSiteRoom.Instance.Options.Count > index &&
                        FindVisibleNode<NRestSiteButton>() != null,
                    "rest site option");
                RestSiteOption option = NRestSiteRoom.Instance!.Options[index];
                NRestSiteButton button = Descendants<NRestSiteButton>(NRestSiteRoom.Instance)
                    .First(b => ReferenceEquals(b.Option, option));
                StartUiTask(InvokeTask(button, "SelectOption", option), input.Label);
                break;
            }
            case RunReplayInputKinds.RewardSelect:
            {
                await WaitUntilReadyAsync(HasCurrentRewardSet, "reward screen");
                Reward reward = FindCurrentReward(input.Payload);
                NRewardButton? button = FindVisibleNodes<NRewardButton>()
                    .FirstOrDefault(b => ReferenceEquals(b.Reward, reward));
                bool isLocalCardReward = reward is CardReward;
                if (isLocalCardReward) _pendingLocalCardRewardChoices++;
                StartUiTask(button != null
                    ? InvokeTask(button, "GetReward")
                    : RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(reward), input.Label,
                    isLocalCardReward ? () => _pendingLocalCardRewardChoices = Math.Max(0, _pendingLocalCardRewardChoices - 1) : null);
                break;
            }
            case RunReplayInputKinds.RewardSkip:
                await WaitUntilReadyAsync(HasCurrentRewardSet, "reward screen");
                RunManager.Instance.RewardsSetSynchronizer.SkipLocalRewardsSet();
                break;
            case RunReplayInputKinds.RewardsProceed:
            {
                await WaitUntilReadyAsync(
                    () => _pendingUiTasks == 0 && FindVisibleNode<NRewardsScreen>() != null,
                    "completed rewards and proceed button");
                NRewardsScreen screen = FindVisibleNode<NRewardsScreen>()!;
                object proceed = AccessTools.Field(typeof(NRewardsScreen), "_proceedButton")!.GetValue(screen)!;
                AccessTools.Method(typeof(NRewardsScreen), "OnProceedButtonPressed")!.Invoke(screen, new[] { proceed });
                break;
            }
            case RunReplayInputKinds.MerchantPurchase:
            {
                await WaitUntilReadyAsync(() => FindVisibleNodes<NMerchantInventory>()
                    .Any(n => n.IsOpen && n.Inventory != null), "merchant inventory");
                MerchantInventory inventory = FindVisibleNodes<NMerchantInventory>()
                    .First(n => n.IsOpen && n.Inventory != null).Inventory!;
                MerchantEntry entry = inventory.AllEntries.ElementAt(int.Parse(input.Payload));
                NMerchantSlot? slot = FindVisibleNodes<NMerchantSlot>()
                    .FirstOrDefault(s => ReferenceEquals(s.Entry, entry));
                StartUiTask(slot != null ? InvokeTask(slot, "OnSelected") : entry.OnTryPurchaseWrapper(inventory), input.Label);
                break;
            }
            case RunReplayInputKinds.MerchantCardRemoval:
            {
                await WaitUntilReadyAsync(() => FindVisibleNodes<NMerchantInventory>()
                    .Any(n => n.IsOpen && n.Inventory?.CardRemovalEntry != null), "merchant card removal");
                MerchantInventory inventory = FindVisibleNodes<NMerchantInventory>()
                    .First(n => n.IsOpen && n.Inventory?.CardRemovalEntry != null).Inventory!;
                MerchantEntry entry = inventory.CardRemovalEntry!;
                NMerchantSlot? slot = FindVisibleNodes<NMerchantSlot>()
                    .FirstOrDefault(s => ReferenceEquals(s.Entry, entry));
                StartUiTask(slot != null ? InvokeTask(slot, "OnSelected") : entry.OnTryPurchaseWrapper(inventory), input.Label);
                break;
            }
            case RunReplayInputKinds.MerchantOpen:
            {
                await WaitUntilReadyAsync(() => FindVisibleNode<NMerchantInventory>() != null, "merchant inventory");
                FindVisibleNode<NMerchantInventory>()!.Open();
                break;
            }
            case RunReplayInputKinds.MerchantClose:
            {
                await WaitUntilReadyAsync(
                    () => _pendingUiTasks == 0 && FindVisibleNodes<NMerchantInventory>().Any(n => n.IsOpen),
                    "completed merchant purchase and open inventory");
                NMerchantInventory inventory = FindVisibleNodes<NMerchantInventory>().First(n => n.IsOpen);
                AccessTools.Method(typeof(NMerchantInventory), "Close")!.Invoke(inventory, null);
                break;
            }
            case RunReplayInputKinds.MerchantProceed:
            {
                await WaitUntilReadyAsync(
                    () => _pendingUiTasks == 0 && FindVisibleNode<NMerchantRoom>() != null,
                    "completed merchant purchase and proceed button");
                NMerchantRoom room = FindVisibleNode<NMerchantRoom>()!;
                object proceed = AccessTools.Field(typeof(NMerchantRoom), "_proceedButton")!.GetValue(room)!;
                AccessTools.Method(typeof(NMerchantRoom), "HideScreen")!.Invoke(room, new[] { proceed });
                break;
            }
            case RunReplayInputKinds.TreasureOpen:
            {
                await WaitUntilReadyAsync(() => FindVisibleNode<NTreasureRoom>() != null, "treasure chest");
                StartUiTask(InvokeTask(FindVisibleNode<NTreasureRoom>()!, "OpenChest"), input.Label);
                break;
            }
            case RunReplayInputKinds.TreasureRelic:
                await WaitUntilReadyAsync(
                    () => FindVisibleNode<NTreasureRoom>() != null &&
                        RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics != null,
                    "opened treasure relic selection");
                RunManager.Instance.TreasureRoomRelicSynchronizer.PickRelicLocally(
                    input.Payload == "skip" ? null : int.Parse(input.Payload));
                break;
            case RunReplayInputKinds.TreasureProceed:
            {
                await WaitUntilReadyAsync(
                    IsTreasureProceedReady,
                    "completed treasure selection and proceed button");
                NTreasureRoom room = FindVisibleNode<NTreasureRoom>()!;
                object proceed = AccessTools.Field(typeof(NTreasureRoom), "_proceedButton")!.GetValue(room)!;
                AccessTools.Method(typeof(NTreasureRoom), "OnProceedButtonPressed")!.Invoke(room, new[] { proceed });
                break;
            }
            case RunReplayInputKinds.AncientDialogue:
            {
                await WaitUntilReadyAsync(() => FindVisibleNode<NAncientEventLayout>() != null, "ancient dialogue");
                NAncientEventLayout layout = FindVisibleNode<NAncientEventLayout>()!;
                object hitbox = AccessTools.Field(typeof(NAncientEventLayout), "_dialogueHitbox")!.GetValue(layout)!;
                AccessTools.Method(typeof(NAncientEventLayout), "OnDialogueHitboxClicked")!.Invoke(layout, new[] { hitbox });
                break;
            }
            case RunReplayInputKinds.FakeMerchantProceed:
            {
                await WaitUntilReadyAsync(() => FindVisibleNode<NFakeMerchant>() != null, "event merchant proceed button");
                NFakeMerchant merchant = FindVisibleNode<NFakeMerchant>()!;
                object proceed = AccessTools.Field(typeof(NFakeMerchant), "_proceedButton")!.GetValue(merchant)!;
                AccessTools.Method(typeof(NFakeMerchant), "HideScreen")!.Invoke(merchant, new[] { proceed });
                break;
            }
            case RunReplayInputKinds.CrystalTool:
            {
                await WaitUntilReadyAsync(() => FindVisibleNode<NCrystalSphereScreen>() != null, "crystal sphere tool");
                NCrystalSphereScreen screen = FindVisibleNode<NCrystalSphereScreen>()!;
                string field = input.Payload == "big" ? "_bigDivinationButton" : "_smallDivinationButton";
                string method = input.Payload == "big" ? "SetBigDivination" : "SetSmallDivination";
                object button = AccessTools.Field(typeof(NCrystalSphereScreen), field)!.GetValue(screen)!;
                AccessTools.Method(typeof(NCrystalSphereScreen), method)!.Invoke(screen, new[] { button });
                break;
            }
            case RunReplayInputKinds.CrystalCell:
            {
                string[] coords = input.Payload.Split(',');
                int x = int.Parse(coords[0]);
                int y = int.Parse(coords[1]);
                await WaitUntilReadyAsync(() => FindVisibleNodes<NCrystalSphereCell>()
                    .Any(c => c.Entity.X == x && c.Entity.Y == y), "crystal sphere cell");
                NCrystalSphereCell cell = FindVisibleNodes<NCrystalSphereCell>()
                    .First(c => c.Entity.X == x && c.Entity.Y == y);
                NCrystalSphereScreen screen = FindVisibleNode<NCrystalSphereScreen>()!;
                StartUiTask(InvokeTask(screen, "OnCellClicked", cell), input.Label);
                break;
            }
            case RunReplayInputKinds.CrystalProceed:
            {
                await WaitUntilReadyAsync(
                    () => _pendingUiTasks == 0 && FindVisibleNode<NCrystalSphereScreen>() != null,
                    "completed crystal sphere selection and proceed button");
                NCrystalSphereScreen screen = FindVisibleNode<NCrystalSphereScreen>()!;
                object proceed = AccessTools.Field(typeof(NCrystalSphereScreen), "_proceedButton")!.GetValue(screen)!;
                AccessTools.Method(typeof(NCrystalSphereScreen), "OnProceedButtonPressed")!.Invoke(screen, new[] { proceed });
                break;
            }
            default:
                throw new InvalidDataException("Unknown replay input kind: " + input.Kind);
        }
        await NGame.Instance!.GetTree().Root.AwaitProcessFrame();
    }

    private void StartUiTask(Task task, string label, Action? onCompleted = null)
    {
        int generation = _sessionGeneration;
        _pendingUiTasks++;
        _ = ObserveUiTaskAsync(task, label, generation, onCompleted);
    }

    private async Task ObserveUiTaskAsync(Task task, string label, int generation, Action? onCompleted)
    {
        try
        {
            await task;
        }
        catch (TaskCanceledException) when (generation != _sessionGeneration)
        {
            // Reloading a floor checkpoint destroys the old room and cancels its UI tasks.
        }
        catch (Exception ex)
        {
            _backgroundUiFailure = ex;
            _pauseRequested = true;
            Log.Error($"[SpireRaceReplay] UI operation '{label}' failed: {ex}");
        }
        finally
        {
            if (generation == _sessionGeneration)
            {
                _pendingUiTasks = Math.Max(0, _pendingUiTasks - 1);
                onCompleted?.Invoke();
            }
        }
    }

    private static Task InvokeTask(object target, string method, params object[] args)
    {
        object? result = AccessTools.Method(target.GetType(), method)!.Invoke(target, args);
        return result as Task ?? Task.CompletedTask;
    }

    private static T? FindVisibleNode<T>() where T : Node
    {
        return FindVisibleNodes<T>().FirstOrDefault();
    }

    private static System.Collections.Generic.IEnumerable<T> FindVisibleNodes<T>() where T : Node
    {
        Node? root = NRun.Instance;
        return root == null ? Enumerable.Empty<T>() : Descendants<T>(root).Where(IsNodeVisible);
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(Node node) where T : Node
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T typed) yield return typed;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static bool IsNodeVisible<T>(T node) where T : Node
    {
        return node is not CanvasItem item || item.IsVisibleInTree();
    }

    private static async Task WaitUntilReadyAsync(Func<bool> condition, string description)
    {
        SceneTree tree = NGame.Instance!.GetTree();
        ulong deadline = Time.GetTicksMsec() + 30000;
        while (Time.GetTicksMsec() < deadline)
        {
            try
            {
                if (condition()) return;
            }
            catch
            {
                // The room and its synchronizers can be replaced between two process frames.
            }
            await tree.Root.AwaitProcessFrame();
        }
        throw new TimeoutException($"Replay timed out waiting for the {description} to become ready.");
    }

    private static bool HasCurrentRewardSet()
    {
        object synchronizer = RunManager.Instance.RewardsSetSynchronizer;
        Player? player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        if (player == null) return false;
        object? playerState = AccessTools.Method(synchronizer.GetType(), "GetRewardStateForPlayer")?
            .Invoke(synchronizer, new object[] { player });
        if (playerState == null) return false;
        return AccessTools.Field(playerState.GetType(), "rewardsStack")?.GetValue(playerState) is IList stack && stack.Count > 0;
    }

    private async Task DeliverNativeEventAsync(CombatReplayEvent replayEvent)
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
                if (_pendingLocalCardRewardChoices > 0)
                {
                    await WaitUntilReadyAsync(HasAwaitingCardRewardSelection, "card reward choice screen");
                    CompleteCardRewardSelection(replayEvent.playerChoiceResult!.Value);
                    await tree.Root.AwaitProcessFrame();
                    break;
                }
                RunManager.Instance.PlayerChoiceSynchronizer.ReceiveReplayChoice(
                    player, replayEvent.choiceId!.Value, replayEvent.playerChoiceResult!.Value);
                break;
            }
            default:
                throw new InvalidEnumArgumentException();
        }
    }

    private static bool HasAwaitingCardRewardSelection()
    {
        NCardRewardSelectionScreen? screen = FindVisibleNode<NCardRewardSelectionScreen>();
        return screen != null && AccessTools.Field(typeof(NCardRewardSelectionScreen), "_completionSource")?.GetValue(screen) != null;
    }

    private bool IsTreasureProceedReady()
    {
        NTreasureRoom? room = FindVisibleNode<NTreasureRoom>();
        if (room == null) return false;
        bool opened = AccessTools.Field(typeof(NTreasureRoom), "_hasChestBeenOpened")?.GetValue(room) as bool? ?? false;
        bool relicCollectionOpen = AccessTools.Field(typeof(NTreasureRoom), "_isRelicCollectionOpen")?.GetValue(room) as bool? ?? true;
        if (!opened) return false;
        // While the relics are open, the same proceed button means "skip" and must be invoked
        // before OpenChest can finish. After a pick, wait for OpenChest to finish its animations.
        return relicCollectionOpen
            ? RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics != null
            : _pendingUiTasks == 0;
    }

    private static void CompleteCardRewardSelection(NetPlayerChoiceResult result)
    {
        if (result.type != PlayerChoiceType.Index)
            throw new InvalidDataException($"Card reward expected an index choice, got {result.type}.");
        NCardRewardSelectionScreen screen = FindVisibleNode<NCardRewardSelectionScreen>()
            ?? throw new InvalidOperationException("The recorded card reward screen is not visible.");
        var completion = AccessTools.Field(typeof(NCardRewardSelectionScreen), "_completionSource")?.GetValue(screen)
            as TaskCompletionSource<int?>
            ?? throw new InvalidOperationException("The card reward screen is not waiting for a selection.");
        int? selectedIndex = result.indexes is { Count: > 0 } ? result.indexes[0] : null;
        if (!completion.TrySetResult(selectedIndex))
            throw new InvalidOperationException("The recorded card reward selection was already completed.");
    }

    private static Reward FindCurrentReward(string payload)
    {
        string[] parts = payload.Split('|', 2);
        int index = int.Parse(parts[0]);
        string? type = parts.ElementAtOrDefault(1);
        object synchronizer = RunManager.Instance.RewardsSetSynchronizer;
        Player player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState())
            ?? throw new InvalidOperationException("Local replay player not found.");
        object playerState = AccessTools.Method(synchronizer.GetType(), "GetRewardStateForPlayer")!.Invoke(synchronizer, new object[] { player })!;
        IList stack = (IList)AccessTools.Field(playerState.GetType(), "rewardsStack")!.GetValue(playerState)!;
        if (stack.Count == 0) throw new InvalidOperationException("No reward set is currently open.");
        object setState = stack[stack.Count - 1]!;
        RewardsSet set = (RewardsSet)AccessTools.Field(setState.GetType(), "set")!.GetValue(setState)!;
        return set.Rewards.FirstOrDefault(r => r.RewardsSetIndex == index && (type == null || r.GetType().FullName == type))
            ?? throw new InvalidOperationException($"Recorded reward {payload} is no longer available.");
    }

    private async Task WaitForStableAsync()
    {
        SceneTree tree = NGame.Instance!.GetTree();
        int stableFrames = 0;
        int choiceFrames = 0;
        ulong deadline = Time.GetTicksMsec() + 60000;
        while (Time.GetTicksMsec() < deadline)
        {
            ActionExecutor executor = RunManager.Instance.ActionExecutor;
            // A combat action which asks for a card/player choice cannot become fully idle until
            // the next recorded PlayerChoice + ResumeAction pair is delivered. Treat this as an
            // input-ready boundary, otherwise playback deadlocks immediately after cards such as
            // Acrobatics, Snap, or any draw/discard-pile selector.
            bool awaitingRecordedChoice = HasActionGatheringPlayerChoice();
            choiceFrames = awaitingRecordedChoice ? choiceFrames + 1 : 0;
            if (choiceFrames >= 2) return;
            bool stable = !executor.IsRunning && executor.CurrentlyRunningAction == null &&
                !CombatManager.Instance.EndingPlayerTurnPhaseOne && !CombatManager.Instance.EndingPlayerTurnPhaseTwo;
            stableFrames = stable ? stableFrames + 1 : 0;
            if (stableFrames >= 3) return;
            await tree.Root.AwaitProcessFrame();
        }
        throw new TimeoutException("Replay did not reach a stable input boundary.");
    }

    private static bool HasActionGatheringPlayerChoice()
    {
        if (RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.State ==
            MegaCrit.Sts2.Core.Entities.Actions.GameActionState.GatheringPlayerChoice)
            return true;

        // The executor can run another player's/hook action after parking the choice action,
        // so CurrentlyRunningAction is not a complete view. Inspect the queue fronts as well.
        object queueSet = RunManager.Instance.ActionQueueSet;
        if (AccessTools.Field(queueSet.GetType(), "_actionQueues")?.GetValue(queueSet) is not IEnumerable queues)
            return false;
        foreach (object queue in queues)
        {
            if (AccessTools.Field(queue.GetType(), "actions")?.GetValue(queue) is not IEnumerable actions)
                continue;
            foreach (object action in actions)
            {
                if (action is GameAction gameAction && gameAction.State ==
                    MegaCrit.Sts2.Core.Entities.Actions.GameActionState.GatheringPlayerChoice)
                    return true;
            }
        }
        return false;
    }

    private async Task DelayRecordedIntervalAsync(long recordedMs)
    {
        long remaining = Math.Min(30000, (long)(recordedMs / _speed));
        while (remaining > 0 && !_pauseRequested)
        {
            int slice = (int)Math.Min(remaining, 33);
            await Task.Delay(slice);
            remaining -= slice;
            _displayElapsedMs = Math.Min(DurationMs, _displayElapsedMs + (long)(slice * _speed));
            UpdateControls();
        }
    }

    private void PauseRuntime()
    {
        if (CombatManager.Instance.IsInProgress) CombatManager.Instance.Pause();
        else RunManager.Instance.ActionExecutor.Pause();
    }

    private void ResumeRuntime()
    {
        RunManager.Instance.ActionExecutor.Unpause();
        if (CombatManager.Instance.IsInProgress) CombatManager.Instance.Unpause();
        Engine.TimeScale = _speed;
    }

    private void UpdateCheckpointIndex()
    {
        if (_timeline == null) return;
        int index = _timeline.Markers.FindLastIndex(m => m.EventIndex <= _nextEventIndex);
        _checkpointIndex = Math.Max(0, index);
    }

    private void ShowControls()
    {
        _controls?.QueueFree();
        _controls = RunReplayControlsOverlay.Create(this);
        NGame.Instance!.AddChild(_controls);
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (_controls == null || _timeline == null) return;
        _controls.UpdateState(_playing, CurrentCheckpoint, MarkerCount, EventIndex, EventCount,
            CurrentElapsedMs, DurationMs, CurrentLabel, _speed, CanTakeOver);
    }
}
