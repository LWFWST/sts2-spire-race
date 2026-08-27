using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2SpireRace.Replay;

public static class ReplayMod
{
    public static ReplayStorage Storage { get; private set; } = null!;
    public static ReplayRecorderCoordinator Recorder { get; private set; } = null!;
    public static ReplayPlaybackController Playback { get; private set; } = null!;
    public static RunReplayPlaybackController RunPlayback { get; private set; } = null!;
    public static ReplayRuntimeMode Mode { get; internal set; }
    public static ReplayBranchManifest? ActiveBranch { get; internal set; }
    public static bool IsInitialized { get; private set; }
    public static string? InitializationError { get; private set; }
    private static bool _profileHookAttached;

    public static bool IsIsolated => Mode is ReplayRuntimeMode.Playback or ReplayRuntimeMode.Branch;

    public static bool TryInitialize()
    {
        if (!SaveManager.Instance.IsProfileInitialized) return false;
        if (IsInitialized) return true;
        try
        {
            InitializeForCurrentProfile();
            BranchSaveRouter.Initialize();
            if (!_profileHookAttached)
            {
                SaveManager.Instance.ProfileIdChanged += OnProfileChanged;
                _profileHookAttached = true;
            }
            IsInitialized = true;
            InitializationError = null;
            Log.Info($"[SpireRaceReplay] Replay Mod initialized for profile {SaveManager.Instance.CurrentProfileId}.");
            return true;
        }
        catch (Exception ex)
        {
            IsInitialized = false;
            InitializationError = ex.Message;
            Log.Error($"[SpireRaceReplay] Initialization failed: {ex}");
            return false;
        }
    }

    private static void InitializeForCurrentProfile()
    {
        Storage = new ReplayStorage();
        Recorder = new ReplayRecorderCoordinator(Storage);
        Playback = new ReplayPlaybackController(Storage);
        RunPlayback = new RunReplayPlaybackController(Storage, Playback);
        Recorder.Start();
        RaceReplayCloudCoordinator.Attach(Recorder);
    }

    private static void OnProfileChanged(int profileId)
    {
        try
        {
            if (IsInitialized) Recorder.Stop();
            IsInitialized = false;
            InitializeForCurrentProfile();
            IsInitialized = true;
            InitializationError = null;
            Log.Info($"[SpireRaceReplay] Switched replay storage to profile {profileId}.");
        }
        catch (Exception ex)
        {
            InitializationError = ex.Message;
            Log.Error($"[SpireRaceReplay] Could not switch replay profile: {ex}");
        }
    }

    public static void ResetRuntimeMode()
    {
        Engine.TimeScale = 1.0;
        BranchStatusOverlay.Hide();
        RuntimeIsolation.Restore();
        Mode = ReplayRuntimeMode.Normal;
        ActiveBranch = null;
    }
}
