using System.Collections.ObjectModel;

namespace Sts2SpireRace.Core;

public sealed class DemoRaceServices : IRaceServices
{
    private readonly object _sync = new();
    private readonly TimeSpan _delay;
    private readonly Random _random = new(240825);
    private readonly List<FriendEntry> _friends;
    private readonly List<TitleDefinition> _titles;
    private readonly List<LeaderboardEntry> _leaderboard;
    private ActivityDefinition _activity;
    private PlayerProfile? _localProfile;
    private CancellationTokenSource? _queueFlow;

    public DemoRaceServices(
        IRacePlatformIdentityProvider identityProvider,
        IRaceSessionLauncher? sessionLauncher = null,
        TimeSpan? demoDelay = null)
    {
        IdentityProvider = identityProvider;
        SessionLauncher = sessionLauncher ?? new DemoSessionLauncher();
        _delay = demoDelay ?? TimeSpan.FromMilliseconds(900);
        _friends = BuildFriends();
        _titles = BuildTitles();
        _leaderboard = BuildLeaderboard();
        _activity = BuildActivity();
    }

    public IRacePlatformIdentityProvider IdentityProvider { get; }
    public IRaceSessionLauncher SessionLauncher { get; }
    public bool IsAuthenticated { get; private set; }
    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        var identity = await IdentityProvider.GetLocalIdentityAsync(cancellationToken);
        if (identity.PlatformId == 0 && !Game.RaceRuntimeInfo.DevelopmentAuthentication)
            throw new InvalidOperationException("A Steam identity is required for Spire Race.");
        IsAuthenticated = true;
    }
    public Task ResumeAsync(CancellationToken cancellationToken = default) => AuthenticateAsync(cancellationToken);
    public Uri? ConfiguredServerUri => null;
    public Task ChangeServerAsync(Uri? serverUri, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public QueueSnapshot CurrentQueue { get; private set; } = new(QueueState.Idle);
    public event Action<QueueSnapshot>? QueueChanged;
    public event Action<RaceInvite>? InviteReceived { add { } remove { } }

    public async Task JoinQueueAsync(QueueRequest request, CancellationToken cancellationToken = default)
    {
        RaceRules.Validate(request.Rules);
        request = request with
        {
            Rules = RaceRules.ApplyCompetitiveMode(request.Rules, request.Kind,
                request.Kind == QueueKind.Ranked ? [request.TeamSize == TeamSize.One ? "Gold" : "Platinum"] : null,
                _random)
        };
        lock (_sync)
        {
            if (CurrentQueue.State is not QueueState.Idle and not QueueState.Completed)
                throw new InvalidOperationException("A queue flow is already active.");
            _queueFlow?.Cancel();
            _queueFlow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        var local = await BuildTeamAsync("local", "Blue", request.TeamSize, true, cancellationToken);
        SetQueue(new QueueSnapshot(QueueState.Searching, request, local, Detail: "searching"));
        _ = AutoFindMatchAsync(_queueFlow.Token);
    }

    public Task CancelQueueAsync(CancellationToken cancellationToken = default)
    {
        _queueFlow?.Cancel();
        SetQueue(new QueueSnapshot(QueueState.Idle, Detail: "cancelled"));
        return Task.CompletedTask;
    }

    public Task ConfirmMatchAsync(bool accepted, CancellationToken cancellationToken = default)
    {
        if (CurrentQueue.State is not QueueState.ReadyCheck and not QueueState.MatchFound)
            return Task.CompletedTask;
        if (!accepted)
        {
            _queueFlow?.Cancel();
            SetQueue(new QueueSnapshot(QueueState.Idle, Detail: "declined"));
            return Task.CompletedTask;
        }

        var local = CurrentQueue.LocalTeam! with
        {
            Participants = CurrentQueue.LocalTeam.Participants
                .Select(p => p with { IsReady = p.IsLocal })
                .ToArray()
        };
        SetQueue(CurrentQueue with { State = QueueState.Lobby, LocalTeam = local, Detail = "lobby" });
        return Task.CompletedTask;
    }

    public async Task SetLocalTeamReadyAsync(bool ready, CancellationToken cancellationToken = default)
    {
        if (CurrentQueue.State != QueueState.Lobby || !ready)
            return;
        var local = CurrentQueue.LocalTeam! with
        {
            Participants = CurrentQueue.LocalTeam.Participants.Select(p => p with { IsReady = true }).ToArray()
        };
        SetQueue(CurrentQueue with { State = QueueState.Starting, LocalTeam = local, Detail = "starting" });
        await DelayAsync(cancellationToken);

        var opponent = CurrentQueue.OpponentTeam!;
        var localTime = TimeSpan.FromMinutes(42) + TimeSpan.FromSeconds(17);
        var opponentTime = TimeSpan.FromMinutes(44) + TimeSpan.FromSeconds(9);
        local = local with { SharedRunTime = localTime };
        opponent = opponent with { SharedRunTime = opponentTime };
        var ratingDelta = CurrentQueue.Request?.Kind == QueueKind.Ranked ? 24 : 0;
        var completedAt = DateTimeOffset.Now;
        var localSide = new SettlementSide(local.Id, local.Name, ParticipantOutcome.Finished, 51, (long)localTime.TotalMilliseconds - 18_000,
            (long)localTime.TotalMilliseconds, 1, 1, 0);
        var enemySide = new SettlementSide(opponent.Id, opponent.Name, ParticipantOutcome.Finished, 51, (long)opponentTime.TotalMilliseconds - 21_000,
            (long)opponentTime.TotalMilliseconds, 0, 2, 1);
        var settlement = new SettlementSnapshot("DEMO-240825", "DEMO-GAME-1", local.Id, FinishReason.BossCompletion,
            localSide, enemySide, ratingDelta, Array.Empty<LegendGameResult>(), "faster-completion", completedAt);
        var result = new RaceResult("DEMO-240825", local, opponent, true, ratingDelta, completedAt, settlement);
        SetQueue(CurrentQueue with
        {
            State = QueueState.Completed,
            LocalTeam = local,
            OpponentTeam = opponent,
            Result = result,
            Detail = "completed"
        });
    }

    public async Task<PlayerProfile> GetLocalProfileAsync(CancellationToken cancellationToken = default)
    {
        if (_localProfile is not null)
            return _localProfile;
        var identity = await IdentityProvider.GetLocalIdentityAsync(cancellationToken);
        _localProfile = BuildProfile(identity.PlatformId.ToString(), identity.DisplayName, true);
        return _localProfile;
    }

    public async Task<PlayerProfile?> GetProfileAsync(string playerId, CancellationToken cancellationToken = default)
    {
        var local = await GetLocalProfileAsync(cancellationToken);
        if (playerId == local.Id)
            return local;
        var friend = _friends.FirstOrDefault(x => x.Id == playerId);
        return friend is null ? null : BuildProfile(friend.Id, friend.DisplayName, false) with
        {
            EquippedTitle = friend.EquippedTitle,
            SoloRank = local.SoloRank with { Tier = friend.RankTier, Points = 41 },
            TeamRank = local.TeamRank with { Tier = friend.RankTier, Points = 67 }
        };
    }

    public async Task<PlayerProfile> UpdateLocalProfileAsync(string displayName, string favoriteCharacter, CancellationToken cancellationToken = default)
    {
        var profile = await GetLocalProfileAsync(cancellationToken);
        _localProfile = profile with { DisplayName = displayName.Trim(), FavoriteCharacter = favoriteCharacter };
        return _localProfile;
    }

    public Task<IReadOnlyList<TitleDefinition>> GetTitlesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TitleDefinition>>(new ReadOnlyCollection<TitleDefinition>(_titles));

    public Task EquipTitleAsync(string titleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(titleId))
        {
            for (var i = 0; i < _titles.Count; i++)
                _titles[i] = _titles[i] with { IsEquipped = false };
            if (_localProfile is not null)
                _localProfile = _localProfile with { EquippedTitle = string.Empty };
            return Task.CompletedTask;
        }
        var index = _titles.FindIndex(x => x.Id == titleId && x.IsUnlocked);
        if (index < 0)
            return Task.CompletedTask;
        for (var i = 0; i < _titles.Count; i++)
            _titles[i] = _titles[i] with { IsEquipped = i == index };
        if (_localProfile is not null)
            _localProfile = _localProfile with { EquippedTitle = _titles[index].Name };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FriendEntry>> GetFriendsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FriendEntry>>(new ReadOnlyCollection<FriendEntry>(_friends));

    public Task<IReadOnlyList<FriendEntry>> SearchPlayersAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FriendEntry>>(_friends.Where(x => x.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(x => x with { Presence = FriendPresence.SearchResult }).ToArray());

    public Task SendFriendRequestAsync(string playerId, CancellationToken cancellationToken = default) =>
        UpdateFriendAsync(playerId, x => x with { Presence = FriendPresence.RequestSent, Activity = "request_sent" });

    public Task AcceptRequestAsync(string playerId, CancellationToken cancellationToken = default) =>
        UpdateFriendAsync(playerId, x => x with { Presence = FriendPresence.Online, Activity = "race_hub" });

    public Task DeclineRequestAsync(string playerId, CancellationToken cancellationToken = default) =>
        RemoveFriendAsync(playerId, cancellationToken);

    public Task RemoveFriendAsync(string playerId, CancellationToken cancellationToken = default)
    {
        _friends.RemoveAll(x => x.Id == playerId);
        return Task.CompletedTask;
    }

    public Task InviteAsync(string playerId, CancellationToken cancellationToken = default) =>
        UpdateFriendAsync(playerId, x => x with { Activity = "invited" });
    public Task RespondToInviteAsync(string playerId, bool accepted, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<LeaderboardEntry>> QueryAsync(
        RankedPool pool,
        bool friendsOnly,
        bool historicalSeason,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<LeaderboardEntry> query = _leaderboard.Where(x => x.Pool == pool);
        if (friendsOnly)
            query = query.Where(x => x.IsFriend);
        if (historicalSeason)
            query = query.Select(x => x with { Rating = x.Rating - 73, Tier = x.Position < 4 ? "Diamond" : x.Tier });
        return Task.FromResult<IReadOnlyList<LeaderboardEntry>>(query.ToArray());
    }

    public Task<ActivityDefinition> GetCurrentActivityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_activity);

    public Task ClaimMissionAsync(string missionId, CancellationToken cancellationToken = default)
    {
        var missions = _activity.Missions.Select(x => x.Id == missionId && x.Progress >= x.Goal
            ? x with { State = ActivityProgressState.Claimed }
            : x).ToArray();
        _activity = _activity with { Missions = missions };
        return Task.CompletedTask;
    }

    public Task ClaimRewardAsync(int level, CancellationToken cancellationToken = default)
    {
        var rewards = _activity.Rewards.Select(x => x.Level == level && x.State == ActivityProgressState.Available
            ? x with { State = ActivityProgressState.Claimed }
            : x).ToArray();
        _activity = _activity with { Rewards = rewards };
        return Task.CompletedTask;
    }

    public Task UploadReplayAsync(RaceReplaySummary replay, byte[] bundle, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<RaceReplaySummary>> GetMatchReplaysAsync(string matchId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RaceReplaySummary>>(Array.Empty<RaceReplaySummary>());

    public Task<IReadOnlyList<SpectatableRace>> GetSpectatableRacesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SpectatableRace>>(Array.Empty<SpectatableRace>());

    public Task<byte[]> DownloadReplayAsync(string matchId, string gameId, string playerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Array.Empty<byte>());

    private async Task AutoFindMatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DelayAsync(cancellationToken);
            var request = CurrentQueue.Request!;
            var opponent = await BuildTeamAsync("opponent", "Red", request.TeamSize, false, cancellationToken);
            SetQueue(CurrentQueue with { State = QueueState.MatchFound, OpponentTeam = opponent, Detail = "match_found" });
            await DelayAsync(cancellationToken, 0.45);
            SetQueue(CurrentQueue with { State = QueueState.ReadyCheck, Detail = "ready_check" });
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal path when the player leaves the queue.
        }
    }

    private async Task<RaceTeam> BuildTeamAsync(
        string id,
        string name,
        TeamSize teamSize,
        bool local,
        CancellationToken cancellationToken)
    {
        var identity = await IdentityProvider.GetLocalIdentityAsync(cancellationToken);
        var names = local
            ? new[] { identity.DisplayName, "Watcher", "Hush", "Spark" }
            : new[] { "Clockwork", "Blue Candle", "Mist Runner", "Neow's Courier" };
        var characters = new[] { "Ironclad", "Silent", "Defect", "Necrobinder" };
        var participants = Enumerable.Range(0, (int)teamSize)
            .Select(i => new RaceParticipant($"{id}-{i}", names[i], characters[i], local && i == 0, false,
                i == 0 ? "Spire Wind" : ""))
            .ToArray();
        return new RaceTeam(id, name, participants);
    }

    private void SetQueue(QueueSnapshot snapshot)
    {
        lock (_sync)
            CurrentQueue = snapshot;
        QueueChanged?.Invoke(snapshot);
    }

    private Task DelayAsync(CancellationToken cancellationToken, double scale = 1) =>
        _delay == TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromMilliseconds(_delay.TotalMilliseconds * scale), cancellationToken);

    private Task UpdateFriendAsync(string playerId, Func<FriendEntry, FriendEntry> update)
    {
        var index = _friends.FindIndex(x => x.Id == playerId);
        if (index >= 0)
            _friends[index] = update(_friends[index]);
        return Task.CompletedTask;
    }

    private static PlayerProfile BuildProfile(string id, string name, bool local)
    {
        var now = DateTimeOffset.Now;
        var history = new[]
        {
            new MatchHistoryEntry("R-1042", QueueKind.Ranked, TeamSize.One, true, TimeSpan.FromMinutes(42.28), "Ironclad", now.AddHours(-3), 24),
            new MatchHistoryEntry("R-1037", QueueKind.Casual, TeamSize.Two, false, TimeSpan.FromMinutes(49.12), "Silent", now.AddDays(-1), 0),
            new MatchHistoryEntry("R-1028", QueueKind.Ranked, TeamSize.Four, true, TimeSpan.FromMinutes(45.75), "Defect", now.AddDays(-2), 18),
            new MatchHistoryEntry("R-1019", QueueKind.Entertainment, TeamSize.Three, true, TimeSpan.FromMinutes(38.4), "Necrobinder", now.AddDays(-4), 0),
            new MatchHistoryEntry("R-1012", QueueKind.Ranked, TeamSize.One, false, TimeSpan.FromMinutes(53.6), "Ironclad", now.AddDays(-6), -17),
            new MatchHistoryEntry("R-1006", QueueKind.Casual, TeamSize.Two, true, TimeSpan.FromMinutes(46.2), "Silent", now.AddDays(-8), 0)
        };
        return new PlayerProfile(
            id,
            name,
            "Spire Wind",
            new RankSnapshot(RankedPool.Solo, "Gold", 2, 68, 24, 15, 0, 318),
            new RankSnapshot(RankedPool.Team, "Platinum", 4, 35, 31, 18, 0, 142),
            "Ironclad",
            0.612,
            TimeSpan.FromMinutes(38.4),
            history,
            local);
    }

    private static List<FriendEntry> BuildFriends() =>
    [
        new("f01", "Watcher", FriendPresence.InRace, "ranked_2v2", "Minute Hand", "Platinum"),
        new("f02", "Hush", FriendPresence.Online, "race_hub", "Spire Wind", "Gold"),
        new("f03", "Spark", FriendPresence.Online, "main_menu", "First Step", "Silver"),
        new("f04", "Clockwork", FriendPresence.Offline, "offline", "No Rest", "Diamond"),
        new("f05", "Blue Candle", FriendPresence.Offline, "offline", "Team Heart", "Gold"),
        new("f06", "Mist Runner", FriendPresence.Request, "friend_request", "", "Bronze"),
        new("f07", "Neow's Courier", FriendPresence.Request, "friend_request", "", "Silver"),
        new("f08", "Ink Bottle", FriendPresence.Online, "entertainment", "Chaos Tamer", "Platinum"),
        new("f09", "Red Mask", FriendPresence.InRace, "ranked_4v4", "Eight as One", "Diamond"),
        new("f10", "Tiny House", FriendPresence.Offline, "offline", "Collector", "Bronze"),
        new("f11", "Sundial", FriendPresence.Online, "casual_1v1", "Minute Hand", "Gold"),
        new("f12", "Oddly Smooth", FriendPresence.Offline, "offline", "First Step", "Silver")
    ];

    private static List<TitleDefinition> BuildTitles()
    {
        string[] names = [
            "First Step", "Spire Wind", "Minute Hand", "No Rest", "Team Heart", "Eight as One",
            "Chaos Tamer", "Perfect Line", "Seed Keeper", "Comeback", "Fog Piercer", "Clock Breaker",
            "Unbroken", "Season Pioneer", "Top One Hundred", "Legend of the Spire"
        ];
        return names.Select((name, i) => new TitleDefinition(
            $"title-{i + 1:00}", name, $"Competitive title: {name}", $"Complete challenge {i + 1}", i < 10, i == 1)).ToList();
    }

    private static List<LeaderboardEntry> BuildLeaderboard()
    {
        var names = new[] { "Clockwork", "Watcher", "Red Mask", "Sundial", "Mist Runner", "Hush", "Blue Candle", "Ink Bottle", "Tiny House", "Spark", "Neow's Courier", "Oddly Smooth" };
        var result = new List<LeaderboardEntry>();
        foreach (var pool in Enum.GetValues<RankedPool>())
        {
            for (var i = 0; i < 24; i++)
            {
                var tier = i < 3 ? "Legend" : i < 8 ? "Diamond" : i < 16 ? "Platinum" : "Gold";
                result.Add(new LeaderboardEntry(i + 1, $"lb-{pool}-{i}", $"{names[i % names.Length]} {i / names.Length + 1}",
                    pool, tier, 2310 - i * 37, 0.72 - i * 0.008, TimeSpan.FromSeconds(2100 + i * 43), i % 4 == 1));
            }
        }
        return result;
    }

    private static ActivityDefinition BuildActivity()
    {
        var missions = new[]
        {
            new ActivityMission("daily-1", "First Bell", "Complete one race", 1, 1, 100, ActivityProgressState.Available),
            new ActivityMission("daily-2", "Against Time", "Finish under 50 minutes", 1, 1, 120, ActivityProgressState.Available),
            new ActivityMission("weekly-1", "Team Climb", "Win three team races", 2, 3, 300, ActivityProgressState.Locked),
            new ActivityMission("weekly-2", "Many Faces", "Race with four characters", 3, 4, 250, ActivityProgressState.Locked)
        };
        var rewards = Enumerable.Range(1, 10).Select(level => new ActivityReward(level,
            level % 3 == 0 ? "Title Seal" : level % 2 == 0 ? "Rank Banner" : "Season Mark",
            level <= 2 ? ActivityProgressState.Available : ActivityProgressState.Locked)).ToArray();
        return new ActivityDefinition("season-mist", "Race Through the Mist",
            "Climb together before the bell disappears into the fog.", DateTimeOffset.Now.AddDays(28), 240, missions, rewards);
    }

    private sealed class DemoSessionLauncher : IRaceSessionLauncher
    {
        public Task LaunchAsync(QueueRequest request, RaceTeam localTeam, RaceTeam opponentTeam, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
