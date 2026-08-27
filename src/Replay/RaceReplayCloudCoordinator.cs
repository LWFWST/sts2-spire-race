using MegaCrit.Sts2.Core.Logging;
using Sts2SpireRace.Core;
using System.Collections.Concurrent;

namespace Sts2SpireRace.Replay;

public static class RaceReplayCloudCoordinator
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim UploadGate = new(1, 1);
    private static readonly SemaphoreSlim LivePublishGate = new(1, 1);
    private static readonly SemaphoreSlim LiveInboxSignal = new(0, int.MaxValue);
    private static readonly ConcurrentQueue<RaceReplayLiveBatch> LiveInbox = new();
    private static readonly SortedDictionary<int, RaceReplayLiveEvent> PendingLiveEvents = new();
    private static int _latestLiveReportedCount;
    private static bool _latestLiveCompleted;
    private static long _lastLiveResubscribeAt;
    private static readonly Dictionary<string, CancellationTokenSource> PendingUploads = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CancellationTokenSource> LiveUploadLoops = new(StringComparer.Ordinal);
    private static ReplayRecorderCoordinator? _recorder;
    private static IReadOnlyList<RaceReplaySummary> _liveTargets = Array.Empty<RaceReplaySummary>();
    private static int _liveTargetIndex;
    private static int _switchingTarget;

    public static bool CanSwitchLiveTarget => _liveTargets.Count > 1;
    public static string CurrentLiveTarget => _liveTargets.ElementAtOrDefault(_liveTargetIndex)?.DisplayName ?? string.Empty;

    public static void Attach(ReplayRecorderCoordinator recorder)
    {
        if (ReferenceEquals(_recorder, recorder)) return;
        if (_recorder is not null)
        {
            _recorder.RunStarted -= OnRunStarted;
            _recorder.InputRecorded -= OnInputRecorded;
            _recorder.CheckpointRecorded -= OnCheckpointRecorded;
            _recorder.RunFinalized -= OnRunFinalized;
        }
        _recorder = recorder;
        recorder.RunStarted += OnRunStarted;
        recorder.InputRecorded += OnInputRecorded;
        recorder.CheckpointRecorded += OnCheckpointRecorded;
        recorder.RunFinalized += OnRunFinalized;
    }

    public static async Task<RunReplayManifest> DownloadAsync(RaceReplaySummary replay, CancellationToken cancellationToken = default)
    {
        if (RaceServiceRegistry.Services is not IRaceReplayService service)
            throw new InvalidOperationException("Replay cloud service is unavailable.");
        byte[] bundle = await service.DownloadReplayAsync(replay.MatchId, replay.GameId, replay.PlayerId, cancellationToken);
        if (bundle.Length == 0) throw new InvalidDataException("Replay bundle is empty.");
        return ReplayMod.Storage.ImportRunBundle(bundle);
    }

    public static async Task WatchAsync(RaceReplaySummary replay, bool live, CancellationToken cancellationToken = default)
    {
        if (!live)
        {
            RunReplayManifest completed = await DownloadAsync(replay, cancellationToken);
            await ReplayMod.RunPlayback.StartAsync(completed);
            return;
        }

        if (RaceServiceRegistry.Services is not IRaceReplayService service)
            throw new InvalidOperationException("Replay cloud service is unavailable.");
        while (LiveInbox.TryDequeue(out _)) { }
        while (LiveInboxSignal.Wait(0)) { }
        PendingLiveEvents.Clear();
        _latestLiveReportedCount = 0;
        _latestLiveCompleted = false;
        _lastLiveResubscribeAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        void OnLiveBatch(RaceReplayLiveBatch batch)
        {
            if (batch.MatchId != replay.MatchId || batch.GameId != replay.GameId || batch.PlayerId != replay.PlayerId)
                return;
            LiveInbox.Enqueue(batch);
            LiveInboxSignal.Release();
        }

        service.ReplayLiveUpdated += OnLiveBatch;
        try
        {
            // Subscribe before taking the snapshot. Reliable WebSocket batches that
            // race with the download are queued and de-duplicated by event index.
            await service.SubscribeReplayLiveAsync(replay.MatchId, replay.GameId, replay.PlayerId, cancellationToken);
            RunReplayManifest manifest = await DownloadAsync(replay, cancellationToken);
            await ReplayMod.RunPlayback.StartAsync(manifest, int.MaxValue);
            ReplayMod.RunPlayback.EnableLiveRefresh(token => RefreshLiveAsync(service, replay, manifest, token));
            await ReplayMod.RunPlayback.PlayAsync();
        }
        finally
        {
            service.ReplayLiveUpdated -= OnLiveBatch;
            try { await service.UnsubscribeReplayLiveAsync(CancellationToken.None); }
            catch (Exception exception) { Log.Warn($"[SpireRaceReplay] Could not unsubscribe live replay: {exception.Message}"); }
        }
    }

    private static async Task<RunReplayManifest?> RefreshLiveAsync(IRaceReplayService service, RaceReplaySummary replay,
        RunReplayManifest manifest, CancellationToken cancellationToken)
    {
        while (true)
        {
            while (LiveInbox.TryDequeue(out RaceReplayLiveBatch? batch))
            {
                if (batch.MatchId != replay.MatchId || batch.GameId != replay.GameId || batch.PlayerId != replay.PlayerId)
                    continue;
                RunReplayInputStream inputs = ReplayMod.Storage.LoadInputStream(
                    ReplayMod.Storage.ResolveRelativePath(manifest.InputFile));
                bool changed = false;
                _latestLiveReportedCount = Math.Max(_latestLiveReportedCount, batch.EventCount);
                _latestLiveCompleted |= batch.Completed;
                foreach (RaceReplayLiveEvent item in batch.Events)
                {
                    if (item.Index < inputs.Events.Count) continue;
                    PendingLiveEvents[item.Index] = item;
                }
                while (PendingLiveEvents.Remove(inputs.Events.Count, out RaceReplayLiveEvent? item))
                {
                    inputs.Events.Add(new RunReplayInputEvent
                    {
                        Index = item.Index,
                        ElapsedMs = item.ElapsedMs,
                        Operation = item.Operation,
                        Kind = item.Kind,
                        Label = item.Label,
                        Payload = item.Payload
                    });
                    changed = true;
                }
                if (changed)
                    ReplayMod.Storage.SaveInputStream(ReplayMod.Storage.ResolveRelativePath(manifest.InputFile), inputs);
                manifest.EventCount = inputs.Events.Count;
                manifest.RaceElapsedMs = batch.RaceElapsedMs;
                manifest.RaceElapsedUpdatedAtUnixMs = batch.RaceElapsedUpdatedAtUnixMs;
                manifest.RaceTimerPaused = batch.RaceTimerPaused;
                manifest.EventSlLimit = batch.EventSlLimit;
                manifest.CombatSlLimit = batch.CombatSlLimit;
                manifest.EventSlUsed = batch.EventSlUsed;
                manifest.CombatSlUsed = batch.CombatSlUsed;
                manifest.MarkerCount = Math.Max(manifest.MarkerCount, batch.MarkerCount);
                if (_latestLiveCompleted && inputs.Events.Count >= _latestLiveReportedCount)
                    manifest.Outcome = "COMPLETED";
                return manifest;
            }
            if (!await LiveInboxSignal.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken))
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _lastLiveResubscribeAt >= 3000)
                {
                    _lastLiveResubscribeAt = now;
                    await service.SubscribeReplayLiveAsync(replay.MatchId, replay.GameId, replay.PlayerId, cancellationToken);
                }
                return null;
            }
        }
    }

    public static async Task WatchMatchAsync(SpectatableRace race, CancellationToken cancellationToken = default)
    {
        if (RaceServiceRegistry.Services is not IRaceReplayService service)
            throw new InvalidOperationException("Replay cloud service is unavailable.");
        var available = (await service.GetMatchReplaysAsync(race.MatchId, cancellationToken))
            .Where(x => x.IsLive && x.EventCount > 0)
            .OrderBy(x => x.TeamId)
            .ThenBy(x => x.PlayerId)
            .ToArray();
        if (available.Length == 0)
        {
            available =
            [
                new RaceReplaySummary(race.MatchId, race.GameId, race.PlayerId, race.DisplayName, string.Empty,
                    string.Empty, race.CharacterId, 1, false, true, race.IsLegendPublic, race.UpdatedAt)
            ];
        }
        _liveTargets = available;
        _liveTargetIndex = Math.Max(0, Array.FindIndex(available,
            x => x.GameId == race.GameId && x.PlayerId == race.PlayerId));
        await WatchAsync(_liveTargets[_liveTargetIndex], true, cancellationToken);
    }

    public static async Task SwitchLiveTargetAsync(int direction)
    {
        if (!CanSwitchLiveTarget || Interlocked.Exchange(ref _switchingTarget, 1) != 0) return;
        try
        {
            _liveTargetIndex = (_liveTargetIndex + direction % _liveTargets.Count + _liveTargets.Count) % _liveTargets.Count;
            ReplayMod.RunPlayback.Exit();
            for (var attempt = 0; attempt < 120 && ReplayMod.RunPlayback.IsPlaying; attempt++)
                await Task.Delay(16);
            await Task.Delay(50);
            await WatchAsync(_liveTargets[_liveTargetIndex], true);
        }
        finally { Interlocked.Exchange(ref _switchingTarget, 0); }
    }

    private static void OnRunStarted(RunReplayManifest run)
    {
        StartLiveUploadLoop(run);
        Schedule(run, TimeSpan.Zero);
        PublishLive(run);
    }
    private static void OnInputRecorded(RunReplayManifest run, RunReplayInputEvent input) => PublishLive(run, input);
    private static void OnCheckpointRecorded(RunReplayManifest run)
    {
        Schedule(run, TimeSpan.Zero);
        PublishLive(run);
    }
    private static void OnRunFinalized(RunReplayManifest run)
    {
        StopLiveUploadLoop(run.RunId);
        PublishLive(run);
        Schedule(run, TimeSpan.Zero);
    }

    private static void StartLiveUploadLoop(RunReplayManifest run)
    {
        if (string.IsNullOrWhiteSpace(run.MatchId) || RaceServiceRegistry.Services.ConfiguredServerUri is null)
            return;
        CancellationTokenSource lifetime;
        lock (Gate)
        {
            if (LiveUploadLoops.ContainsKey(run.RunId)) return;
            lifetime = new CancellationTokenSource();
            LiveUploadLoops[run.RunId] = lifetime;
        }
        _ = LiveUploadLoopAsync(run, lifetime);
    }

    private static async Task LiveUploadLoopAsync(RunReplayManifest run, CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested && run.Outcome == "IN_PROGRESS")
            {
                await Task.Delay(500, lifetime.Token);
                PublishLive(run);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (Gate)
            {
                if (LiveUploadLoops.TryGetValue(run.RunId, out var current) && ReferenceEquals(current, lifetime))
                    LiveUploadLoops.Remove(run.RunId);
            }
            lifetime.Dispose();
        }
    }

    private static void PublishLive(RunReplayManifest run, RunReplayInputEvent? input = null)
    {
        if (string.IsNullOrWhiteSpace(run.MatchId) || RaceServiceRegistry.Services.ConfiguredServerUri is null)
            return;
        _ = PublishLiveAsync(run, input);
    }

    private static async Task PublishLiveAsync(RunReplayManifest run, RunReplayInputEvent? input)
    {
        try
        {
            await LivePublishGate.WaitAsync();
            try
            {
                ReplayMod.Recorder.RefreshRaceSnapshot();
                RaceReplayLiveEvent[] events = input is null ? Array.Empty<RaceReplayLiveEvent>() :
                [new(input.Index, input.ElapsedMs, input.Operation, input.Kind, input.Label, input.Payload)];
                var batch = new RaceReplayLiveBatch(run.MatchId, run.GameId, run.PlayerId, run.TeamId, run.RunId,
                    run.Character, run.EventCount, run.RaceElapsedMs, run.RaceElapsedUpdatedAtUnixMs,
                    run.RaceTimerPaused, run.EventSlLimit, run.CombatSlLimit, run.EventSlUsed, run.CombatSlUsed,
                    run.MarkerCount, run.Outcome != "IN_PROGRESS", events);
                await ((IRaceReplayService)RaceServiceRegistry.Services).PublishReplayLiveAsync(batch);
            }
            finally { LivePublishGate.Release(); }
        }
        catch (Exception exception)
        {
            Log.Warn($"[SpireRaceReplay] Live action publication deferred: {exception.Message}");
        }
    }

    private static void StopLiveUploadLoop(string runId)
    {
        lock (Gate)
        {
            if (LiveUploadLoops.Remove(runId, out var lifetime))
                lifetime.Cancel();
        }
    }

    private static void Schedule(RunReplayManifest run, TimeSpan delay)
    {
        if (string.IsNullOrWhiteSpace(run.MatchId) || RaceServiceRegistry.Services.ConfiguredServerUri is null)
            return;
        CancellationTokenSource lifetime;
        lock (Gate)
        {
            // Keep the earliest scheduled upload. Repeated actions must not
            // postpone it indefinitely, which previously starved live viewers.
            if (PendingUploads.ContainsKey(run.RunId)) return;
            lifetime = new CancellationTokenSource();
            PendingUploads[run.RunId] = lifetime;
        }
        _ = UploadAfterDelayAsync(run, delay, lifetime);
    }

    private static async Task UploadAfterDelayAsync(RunReplayManifest run, TimeSpan delay, CancellationTokenSource lifetime)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, lifetime.Token);
            await UploadGate.WaitAsync(lifetime.Token);
            try
            {
                ReplayMod.Recorder.PrepareForCloudUpload();
                byte[] bundle = run.Outcome == "IN_PROGRESS"
                    ? ReplayMod.Storage.CreateLiveRunBundle(run)
                    : ReplayMod.Storage.CreateRunBundle(run);
                var summary = new RaceReplaySummary(run.MatchId, run.GameId, run.PlayerId, string.Empty, run.TeamId,
                    run.RunId, run.Character, run.EventCount, run.Outcome != "IN_PROGRESS", run.Outcome == "IN_PROGRESS",
                    false, DateTimeOffset.UtcNow);
                await ((IRaceReplayService)RaceServiceRegistry.Services).UploadReplayAsync(summary, bundle, lifetime.Token);
            }
            finally { UploadGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Log.Warn($"[SpireRaceReplay] Cloud replay upload deferred: {exception.Message}");
        }
        finally
        {
            lock (Gate)
            {
                if (PendingUploads.TryGetValue(run.RunId, out var current) && ReferenceEquals(current, lifetime))
                    PendingUploads.Remove(run.RunId);
            }
            lifetime.Dispose();
        }
    }
}
