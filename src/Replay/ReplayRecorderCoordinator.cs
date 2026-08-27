using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Sts2SpireRace.Game;
using Sts2SpireRace.Core;

namespace Sts2SpireRace.Replay;

public sealed class ReplayRecorderCoordinator
{
    public event Action<RunReplayManifest>? RunStarted;
    public event Action<RunReplayManifest, RunReplayInputEvent>? InputRecorded;
    public event Action<RunReplayManifest>? CheckpointRecorded;
    public event Action<RunReplayManifest>? RunFinalized;

    public RunReplayManifest? ActiveRun => _run;
    private readonly ReplayStorage _storage;
    private ReplayCatalog _catalog;
    private RunReplayManifest? _run;
    private CombatReplayManifest? _combat;
    private ReplayTimeline? _timeline;
    private RunReplayTimeline? _runTimeline;
    private RunReplayInputStream? _inputStream;
    private long _combatStartedAt;
    private long _runStartedAt;
    private bool _combatWon;
    private ActionExecutor? _actionExecutor;
    private PlayerChoiceSynchronizer? _choiceSynchronizer;
    private ActionQueueSet? _actionQueueSet;
    private int _operationIndex;
    private int _lastCheckpointFloor = -1;
    private bool _started;

    public ReplayRecorderCoordinator(ReplayStorage storage)
    {
        _storage = storage;
        _catalog = storage.LoadCatalog();
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        RecoverPartials();
        RunManager.Instance.RunStarted += OnRunStarted;
        RunManager.Instance.RoomEntered += OnRoomEntered;
        RunManager.Instance.RoomExited += OnRoomExited;
        RunManager.Instance.ActEntered += OnActEntered;
        CombatManager.Instance.CombatSetUp += OnCombatBegan;
        CombatManager.Instance.CombatWon += OnCombatWon;
        CombatManager.Instance.CombatEnded += OnCombatEnded;
        CombatManager.Instance.TurnStarted += OnTurnStarted;
        CombatManager.Instance.TurnEnded += OnTurnEnded;
        Log.Info($"[SpireRaceReplay] Recorder ready. Storage: {_storage.AbsoluteRoot}");
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        RunManager.Instance.RunStarted -= OnRunStarted;
        RunManager.Instance.RoomEntered -= OnRoomEntered;
        RunManager.Instance.RoomExited -= OnRoomExited;
        RunManager.Instance.ActEntered -= OnActEntered;
        CombatManager.Instance.CombatSetUp -= OnCombatBegan;
        CombatManager.Instance.CombatWon -= OnCombatWon;
        CombatManager.Instance.CombatEnded -= OnCombatEnded;
        CombatManager.Instance.TurnStarted -= OnTurnStarted;
        CombatManager.Instance.TurnEnded -= OnTurnEnded;
        if (_actionExecutor != null) _actionExecutor.AfterActionExecuted -= OnActionExecuted;
        if (_choiceSynchronizer != null) _choiceSynchronizer.PlayerChoiceReceived -= OnPlayerChoiceReceived;
        if (_actionQueueSet != null)
        {
            _actionQueueSet.ActionEnqueued -= OnActionEnqueued;
            _actionQueueSet.ActionResumed -= OnActionResumed;
        }
        _actionExecutor = null;
        _choiceSynchronizer = null;
        _actionQueueSet = null;
    }

    public void AttachRunHooks()
    {
        if (_actionExecutor != null)
        {
            _actionExecutor.AfterActionExecuted -= OnActionExecuted;
        }
        if (_choiceSynchronizer != null)
        {
            _choiceSynchronizer.PlayerChoiceReceived -= OnPlayerChoiceReceived;
        }
        if (_actionQueueSet != null)
        {
            _actionQueueSet.ActionEnqueued -= OnActionEnqueued;
            _actionQueueSet.ActionResumed -= OnActionResumed;
        }
        _actionExecutor = RunManager.Instance.ActionExecutor;
        _choiceSynchronizer = RunManager.Instance.PlayerChoiceSynchronizer;
        _actionQueueSet = RunManager.Instance.ActionQueueSet;
        if (_actionExecutor != null)
        {
            _actionExecutor.AfterActionExecuted += OnActionExecuted;
        }
        if (_choiceSynchronizer != null)
        {
            _choiceSynchronizer.PlayerChoiceReceived += OnPlayerChoiceReceived;
        }
        if (_actionQueueSet != null)
        {
            _actionQueueSet.ActionEnqueued += OnActionEnqueued;
            _actionQueueSet.ActionResumed += OnActionResumed;
        }
    }

    public void FlushPartial()
    {
        if (_combat == null || ReplayMod.Mode != ReplayRuntimeMode.Normal)
        {
            return;
        }
        try
        {
            string final = _storage.ResolveRelativePath(_combat.ReplayFile);
            string partial = Path.Combine(Path.GetDirectoryName(final)!, Path.GetFileNameWithoutExtension(final) + ".partial.mcr");
            NativeReplayAdapter.FlushCurrent(partial);
            string timeline = _storage.ResolveRelativePath(_combat.TimelineFile);
            if (_timeline != null) _storage.SaveTimeline(timeline, _timeline);
            if (File.Exists(partial)) _combat.FileSize = new FileInfo(partial).Length;
            PersistManifests();
        }
        catch (Exception ex)
        {
            Log.Error($"[SpireRaceReplay] Failed to flush partial replay: {ex}");
        }
    }

    public void FinalizeActiveAsIncomplete()
    {
        if (_combat != null)
        {
            FinalizeCombat("INCOMPLETE");
        }
        FinalizeRun("INCOMPLETE");
    }

    public void FinalizeRun(string outcome)
    {
        if (_run == null || _runTimeline == null || _run.Outcome != "IN_PROGRESS") return;
        _run.Outcome = outcome;
        _run.EndedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _run.MarkerCount = _runTimeline.Markers.Count;
        _run.EventCount = _inputStream?.Events.Count ?? 0;
        PersistManifests();
        try
        {
            string dir = _storage.GetRunDirectory(_run.RunId);
            _run.FileSize = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
            PersistManifests();
        }
        catch (Exception ex)
        {
            Log.Warn($"[SpireRaceReplay] Could not calculate run replay size: {ex.Message}");
        }
        RunReplayManifest finalized = _run;
        RunFinalized?.Invoke(finalized);
        _run = null;
        _runTimeline = null;
        _inputStream = null;
    }

    public void RecordExternalOperation(string kind, string label, string payload)
    {
        if (_run == null || _inputStream == null || ReplayMod.Mode != ReplayRuntimeMode.Normal) return;
        _operationIndex++;
        AppendInput(kind, label, payload, _operationIndex);
    }

    public void UpdateRaceSlUsage(int eventSlUsed, int combatSlUsed)
    {
        if (_run == null) return;
        _run.EventSlUsed = Math.Max(0, eventSlUsed);
        _run.CombatSlUsed = Math.Max(0, combatSlUsed);
        RefreshRaceSnapshot();
        PersistManifests();
        CheckpointRecorded?.Invoke(_run);
    }

    public void RefreshRaceSnapshot()
    {
        if (_run == null) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var match = RaceActiveSession.Current;
        if (match is not null)
        {
            _run.EventSlLimit = match.Rules.EventSlLimit;
            _run.CombatSlLimit = match.Rules.CombatSlLimit;
            _run.EventSlUsed = RaceTelemetrySequence.EventSlUsed(match.GameId);
            _run.CombatSlUsed = RaceTelemetrySequence.CombatSlUsed(match.GameId);
        }
        if (RaceServiceRegistry.Services is IRaceClockService { CurrentClock: { IsSynchronized: true } clock })
        {
            _run.RaceElapsedMs = Math.Max(0, clock.ElapsedMilliseconds +
                (clock.IsPaused ? 0 : now - clock.ServerUnixMilliseconds));
            _run.RaceTimerPaused = clock.IsPaused;
        }
        else if (match is not null)
        {
            _run.RaceElapsedMs = Math.Max(0, now - match.StartedAtUnixMilliseconds);
            _run.RaceTimerPaused = false;
        }
        else
        {
            _run.RaceElapsedMs = Math.Max(0, now - _runStartedAt);
            _run.RaceTimerPaused = false;
        }
        _run.RaceElapsedUpdatedAtUnixMs = now;
    }

    public void PrepareForCloudUpload()
    {
        RefreshRaceSnapshot();
        PersistManifests();
    }

    private void OnRunStarted(RunState state)
    {
        AttachRunHooks();
        MatchAssignment? match = RaceActiveSession.Current;
        if (ReplayMod.Mode != ReplayRuntimeMode.Normal || match is null)
        {
            return;
        }
        Player? player = LocalContext.GetMe(state);
        if (player == null) return;
        long startTime;
        try { startTime = RunManager.Instance.ToSave(null).StartTime; }
        catch { startTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }
        string playerId = match.LocalTeam.Participants.FirstOrDefault(participant => participant.IsLocal)?.Id
            ?? player.NetId.ToString();
        string runId = StableId($"{match.MatchId}|{match.GameId}|{playerId}|{state.Rng.StringSeed}|{startTime}");
        _runStartedAt = startTime * 1000;
        _run = _catalog.Runs.FirstOrDefault(r => r.RunId == runId);
        if (_run != null && _run.Compatibility.FormatVersion < 5)
        {
            runId += "_v5_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _run = null;
        }
        if (_run == null)
        {
            _run = new RunReplayManifest
            {
                MatchId = match.MatchId,
                GameId = match.GameId,
                PlayerId = playerId,
                TeamId = match.LocalTeam.Id,
                RunId = runId,
                Seed = state.Rng.StringSeed,
                Character = player.Character.Id.ToString(),
                Ascension = state.AscensionLevel,
                GameMode = state.GameMode.ToString(),
                StartedAtUnixMs = _runStartedAt,
                Compatibility = CompatibilityService.Capture()
            };
            string runTimelinePath = Path.Combine(_storage.GetRunDirectory(runId), "timeline.json");
            _run.TimelineFile = ReplayStorage.ToRelativePath(_storage.AbsoluteRoot, runTimelinePath);
            string inputPath = Path.Combine(_storage.GetRunDirectory(runId), "inputs.json");
            _run.InputFile = ReplayStorage.ToRelativePath(_storage.AbsoluteRoot, inputPath);
            _catalog.Runs.Add(_run);
        }
        RefreshRaceSnapshot();
        string timelinePath = string.IsNullOrEmpty(_run.TimelineFile)
            ? Path.Combine(_storage.GetRunDirectory(runId), "timeline.json")
            : _storage.ResolveRelativePath(_run.TimelineFile);
        _run.TimelineFile = ReplayStorage.ToRelativePath(_storage.AbsoluteRoot, timelinePath);
        _runTimeline = File.Exists(timelinePath)
            ? _storage.LoadRunTimeline(timelinePath)
            : new RunReplayTimeline { RunId = runId };
        string inputPathResolved = string.IsNullOrEmpty(_run.InputFile)
            ? Path.Combine(_storage.GetRunDirectory(runId), "inputs.json")
            : _storage.ResolveRelativePath(_run.InputFile);
        _run.InputFile = ReplayStorage.ToRelativePath(_storage.AbsoluteRoot, inputPathResolved);
        _inputStream = File.Exists(inputPathResolved)
            ? _storage.LoadInputStream(inputPathResolved)
            : new RunReplayInputStream { RunId = runId };
        _operationIndex = _inputStream.Events.LastOrDefault()?.Operation ?? 0;
        _lastCheckpointFloor = _runTimeline.Markers.LastOrDefault()?.Floor ?? -1;
        PersistManifests();
        RunStarted?.Invoke(_run);
    }

    public void CaptureFloorBoundary(AbstractRoom? preFinishedRoom)
    {
        if (preFinishedRoom != null || _run == null || _runTimeline == null || ReplayMod.Mode != ReplayRuntimeMode.Normal)
            return;
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (state?.Map == null || state.TotalFloor <= _lastCheckpointFloor) return;
        try
        {
            SerializableRun save = RunManager.Instance.ToSave(null);
            if (!ReplayStorage.HasCompleteCurrentMap(save))
            {
                Log.Warn($"[SpireRaceReplay] Delaying floor {state.TotalFloor} checkpoint because the map is not complete yet.");
                return;
            }
            int markerCount = _runTimeline.Markers.Count;
            AddRunMarker(state.TotalFloor == 0 ? "Run start boundary" : $"Floor {state.TotalFloor} boundary", save);
            if (_runTimeline.Markers.Count > markerCount)
            {
                var map = save.Acts[save.CurrentActIndex].SavedMap;
                Log.Info($"[SpireRaceReplay] Captured floor {state.TotalFloor} boundary with {map!.Points.Count} original map nodes; start={map.StartingPoint.PointType}.");
            }
            PersistManifests();
            CheckpointRecorded?.Invoke(_run);
        }
        catch (Exception ex)
        {
            Log.Error($"[SpireRaceReplay] Failed to capture floor boundary: {ex}");
        }
    }

    private void OnCombatBegan(CombatState state)
    {
        if (ReplayMod.Mode != ReplayRuntimeMode.Normal)
        {
            return;
        }
        if (_run == null)
        {
            OnRunStarted((RunState)state.RunState);
        }
        if (_run == null) return;
        _combatWon = false;
        _combatStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string combatId = $"combat_{state.RunState.TotalFloor:D3}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
        string dir = _storage.GetCombatDirectory(_run.RunId);
        string replayPath = Path.Combine(dir, combatId + ".mcr");
        string timelinePath = Path.Combine(dir, combatId + ".timeline.json");
        _combat = new CombatReplayManifest
        {
            CombatId = combatId,
            RunId = _run.RunId,
            Act = state.RunState.CurrentActIndex + 1,
            Floor = state.RunState.TotalFloor,
            Encounter = state.Encounter?.Id.ToString() ?? "UNKNOWN",
            StartedAtUnixMs = _combatStartedAt,
            ReplayFile = ReplayStorage.ToRelativePath(_storage.AbsoluteRoot, replayPath),
            TimelineFile = ReplayStorage.ToRelativePath(_storage.AbsoluteRoot, timelinePath),
            Compatibility = CompatibilityService.Capture()
        };
        _timeline = new ReplayTimeline { CombatId = combatId };
        _run.Combats.Add(_combat);
        AddStableMarker("Combat initial state", force: true);
        Log.Info($"[SpireRaceReplay] Recording {combatId} ({_combat.Encounter}).");
    }

    private void OnActionExecuted(GameAction action)
    {
        if (_combat != null) AddStableMarker(action.GetType().Name);
    }

    private void OnActionEnqueued(GameAction action)
    {
        if (_run == null || _inputStream == null || ReplayMod.Mode != ReplayRuntimeMode.Normal)
            return;
        try
        {
            bool userOperation = IsUserOperation(action);
            if (userOperation) _operationIndex++;
            CombatReplayEvent replayEvent = NativeReplayAdapter.FromGameAction(action);
            AppendInput(RunReplayInputKinds.Native, action.GetType().Name,
                NativeReplayAdapter.SerializeEvent(replayEvent), Math.Max(1, _operationIndex));
        }
        catch (Exception ex)
        {
            Log.Error($"[SpireRaceReplay] Could not record action {action}: {ex}");
        }
    }

    private void OnActionResumed(uint actionId)
    {
        if (_run == null || _inputStream == null || ReplayMod.Mode != ReplayRuntimeMode.Normal)
            return;
        CombatReplayEvent replayEvent = new()
        {
            eventType = MegaCrit.Sts2.Core.Multiplayer.Replay.CombatReplayEventType.ResumeAction,
            actionId = actionId
        };
        AppendInput(RunReplayInputKinds.Native, "Resume action", NativeReplayAdapter.SerializeEvent(replayEvent), Math.Max(1, _operationIndex));
    }

    private void OnTurnStarted(CombatState state) => AddStableMarker($"{state.CurrentSide} turn started");

    private void OnTurnEnded(CombatState state) => AddStableMarker($"{state.CurrentSide} turn ended");

    private void OnPlayerChoiceReceived(Player player, uint choiceId, MegaCrit.Sts2.Core.Entities.Multiplayer.NetPlayerChoiceResult result)
    {
        if (LocalContext.IsMe(player) && _run != null && _inputStream != null && ReplayMod.Mode == ReplayRuntimeMode.Normal)
        {
            _operationIndex++;
            CombatReplayEvent replayEvent = new()
            {
                eventType = MegaCrit.Sts2.Core.Multiplayer.Replay.CombatReplayEventType.PlayerChoice,
                playerId = player.NetId,
                choiceId = choiceId,
                playerChoiceResult = result
            };
            AppendInput(RunReplayInputKinds.Native, $"Player choice {choiceId}", NativeReplayAdapter.SerializeEvent(replayEvent), _operationIndex);
            if (_combat != null) AddStableMarker($"Player choice {choiceId}");
        }
    }

    private void OnCombatWon(CombatRoom _)
    {
        _combatWon = true;
    }

    private void OnCombatEnded(CombatRoom _)
    {
        if (_combat == null) return;
        Player? player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        string outcome = _combatWon ? "WIN" : (player?.Creature.IsDead == true ? "LOSS" : "ENDED");
        FinalizeCombat(outcome);
    }

    private void AddStableMarker(string label, bool force = false)
    {
        if (_combat == null || _timeline == null || ReplayMod.Mode != ReplayRuntimeMode.Normal)
        {
            return;
        }
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        RunState? run = RunManager.Instance.DebugOnlyGetState();
        if (state == null || run == null) return;
        Player? player = LocalContext.GetMe(run);
        int eventCount = NativeReplayAdapter.GetEventCount();
        uint hash = NativeReplayAdapter.CalculateCurrentStateHash();
        ReplayMarker marker = new()
        {
            Index = _timeline.Markers.Count,
            EventCount = eventCount,
            ChecksumId = NativeReplayAdapter.GetLastChecksumId(),
            Round = state.RoundNumber,
            Side = state.CurrentSide.ToString(),
            Turn = player?.PlayerCombatState?.TurnNumber ?? 0,
            Label = label,
            StateHash = hash,
            ElapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _combatStartedAt
        };
        ReplayMarker? previous = _timeline.Markers.LastOrDefault();
        if (!force && previous != null && previous.EventCount == marker.EventCount && previous.StateHash == marker.StateHash)
        {
            return;
        }
        _timeline.Markers.Add(marker);
        _combat.MarkerCount = _timeline.Markers.Count;
        FlushPartial();
    }

    private void OnRoomEntered()
    {
    }

    private void OnRoomExited() { }

    private void OnActEntered() { }

    private void AddRunMarker(string label, SerializableRun save)
    {
        if (_run == null || _runTimeline == null || ReplayMod.Mode != ReplayRuntimeMode.Normal) return;
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (state == null) return;
        try
        {
            RefreshRaceSnapshot();
            string? checkpointFile = null;
            string json = JsonSerializationUtility.ToJson(save);
            uint hash = BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            string absolute = Path.Combine(_storage.GetRunDirectory(_run.RunId), "checkpoints", $"{_runTimeline.Markers.Count:D7}.save");
            _storage.SaveRunCheckpoint(absolute, save);
            checkpointFile = ReplayStorage.ToRelativePath(_storage.AbsoluteRoot, absolute);
            RunReplayMarker marker = new()
            {
                Index = _runTimeline.Markers.Count,
                ElapsedMs = _run.RaceElapsedMs,
                Act = state.CurrentActIndex + 1,
                Floor = state.TotalFloor,
                Room = state.CurrentRoom?.RoomType.ToString() ?? "Map",
                Label = label,
                StateHash = hash,
                CheckpointFile = checkpointFile,
                EventIndex = _inputStream?.Events.Count ?? 0,
                NextActionId = RunManager.Instance.ActionQueueSet.NextActionId,
                NextHookId = RunManager.Instance.ActionQueueSynchronizer.NextHookId,
                NextChecksumId = RunManager.Instance.ChecksumTracker.NextId,
                ChoiceIds = RunManager.Instance.PlayerChoiceSynchronizer.ChoiceIds.ToList(),
                RewardIds = RunManager.Instance.RewardsSetSynchronizer.GetNextRewardIds().ToList()
            };
            RunReplayMarker? previous = _runTimeline.Markers.LastOrDefault();
            if (previous != null && previous.Floor == marker.Floor && previous.StateHash == marker.StateHash) return;
            _runTimeline.Markers.Add(marker);
            _lastCheckpointFloor = marker.Floor;
            _run.MarkerCount = _runTimeline.Markers.Count;
            _storage.SaveRunTimeline(_storage.ResolveRelativePath(_run.TimelineFile), _runTimeline);
        }
        catch (Exception ex)
        {
            Log.Error($"[SpireRaceReplay] Failed to capture run marker '{label}': {ex}");
        }
    }

    private void AppendInput(string kind, string label, string payload, int operation)
    {
        if (_run == null || _inputStream == null) return;
        RefreshRaceSnapshot();
        RunReplayInputEvent input = new()
        {
            Index = _inputStream.Events.Count,
            ElapsedMs = _run.RaceElapsedMs,
            Operation = operation,
            Kind = kind,
            Label = label,
            Payload = payload
        };
        _inputStream.Events.Add(input);
        _run.EventCount = _inputStream.Events.Count;
        PersistManifests();
        InputRecorded?.Invoke(_run, input);
    }

    private static bool IsUserOperation(GameAction action)
    {
        return action is PlayCardAction
            or EndPlayerTurnAction
            or UndoEndPlayerTurnAction
            or UsePotionAction
            or VoteForMapCoordAction;
    }

    private void FinalizeCombat(string outcome)
    {
        if (_combat == null) return;
        FlushPartial();
        string final = _storage.ResolveRelativePath(_combat.ReplayFile);
        string partial = Path.Combine(Path.GetDirectoryName(final)!, Path.GetFileNameWithoutExtension(final) + ".partial.mcr");
        try
        {
            if (File.Exists(partial))
            {
                if (File.Exists(final)) File.Delete(final);
                File.Move(partial, final);
                _combat.FileSize = new FileInfo(final).Length;
            }
            _combat.EndedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _combat.Outcome = outcome;
            PersistManifests();
            Log.Info($"[SpireRaceReplay] Finalized {_combat.CombatId}: {outcome}, {_combat.MarkerCount} markers.");
        }
        catch (Exception ex)
        {
            Log.Error($"[SpireRaceReplay] Failed to finalize {_combat.CombatId}: {ex}");
        }
        finally
        {
            _combat = null;
            _timeline = null;
        }
    }

    private void PersistManifests()
    {
        if (_run != null && _runTimeline != null && !string.IsNullOrEmpty(_run.TimelineFile))
            _storage.SaveRunTimeline(_storage.ResolveRelativePath(_run.TimelineFile), _runTimeline);
        if (_run != null && _inputStream != null && !string.IsNullOrEmpty(_run.InputFile))
            _storage.SaveInputStream(_storage.ResolveRelativePath(_run.InputFile), _inputStream);
        if (_run != null) _storage.SaveRunManifest(_run);
        _storage.SaveCatalog(_catalog);
    }

    private void RecoverPartials()
    {
        foreach (RunReplayManifest run in _catalog.Runs)
        {
            bool changed = false;
            foreach (CombatReplayManifest combat in run.Combats.Where(c => c.Outcome == "IN_PROGRESS"))
            {
                try
                {
                    string final = _storage.ResolveRelativePath(combat.ReplayFile);
                    string partial = Path.Combine(Path.GetDirectoryName(final)!, Path.GetFileNameWithoutExtension(final) + ".partial.mcr");
                    if (!File.Exists(partial)) continue;
                    _ = NativeReplayAdapter.ReadReplay(partial);
                    if (File.Exists(final)) File.Delete(final);
                    File.Move(partial, final);
                    combat.Outcome = "INCOMPLETE";
                    combat.EndedAtUnixMs = combat.StartedAtUnixMs;
                    combat.FileSize = new FileInfo(final).Length;
                    changed = true;
                }
                catch (Exception ex)
                {
                    Log.Warn($"[SpireRaceReplay] Partial replay for {combat.CombatId} is not recoverable: {ex.Message}");
                }
            }
            if (changed) _storage.SaveRunManifest(run);
        }
        _storage.SaveCatalog(_catalog);
    }

    private static string StableId(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "run_" + Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant();
    }
}
