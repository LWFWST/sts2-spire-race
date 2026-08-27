using MegaCrit.Sts2.Core.Logging;
using Sts2SpireRace.Core;

namespace Sts2SpireRace.Replay;

public static class RaceReplayCloudCoordinator
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim UploadGate = new(1, 1);
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
        RunReplayManifest manifest = await DownloadAsync(replay, cancellationToken);
        await ReplayMod.RunPlayback.StartAsync(manifest, live ? int.MaxValue : 0);
        if (!live) return;
        var lastUpdated = replay.UpdatedAt;
        var lastEventCount = replay.EventCount;
        ReplayMod.RunPlayback.EnableLiveRefresh(async token =>
        {
            if (RaceServiceRegistry.Services is not IRaceReplayService service)
                return null;
            var latest = (await service.GetMatchReplaysAsync(replay.MatchId, token))
                .FirstOrDefault(x => x.GameId == replay.GameId && x.PlayerId == replay.PlayerId);
            if (latest is null || (latest.UpdatedAt <= lastUpdated && latest.EventCount <= lastEventCount))
                return null;
            lastUpdated = latest.UpdatedAt;
            lastEventCount = latest.EventCount;
            return await DownloadAsync(latest, token);
        });
        await ReplayMod.RunPlayback.PlayAsync();
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
    }
    private static void OnInputRecorded(RunReplayManifest run, RunReplayInputEvent input) => Schedule(run, TimeSpan.FromMilliseconds(200));
    private static void OnCheckpointRecorded(RunReplayManifest run) => Schedule(run, TimeSpan.Zero);
    private static void OnRunFinalized(RunReplayManifest run)
    {
        StopLiveUploadLoop(run.RunId);
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
                await Task.Delay(1000, lifetime.Token);
                Schedule(run, TimeSpan.Zero);
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
