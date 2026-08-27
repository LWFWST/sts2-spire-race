namespace Sts2SpireRace.Core;

public enum QueueKind
{
    Casual,
    Ranked,
    Entertainment
}

public enum TeamSize
{
    One = 1,
    Two = 2,
    Three = 3,
    Four = 4
}

public enum RankedPool
{
    Solo,
    Team
}

public enum QueueState
{
    Idle,
    Searching,
    MatchFound,
    ReadyCheck,
    Lobby,
    Draft,
    Starting,
    FinishPending,
    Completed
}

public enum MatchPhase
{
    Waiting,
    ReadyCheck,
    Running,
    DeathDecision,
    Saved,
    Intermission,
    Completed
}

public enum ParticipantOutcome
{
    Active,
    ScoreLocked,
    Finished,
    Surrendered,
    Forfeited,
    TimedOut
}

public enum FinishReason
{
    BossCompletion,
    HighestFloor,
    EarlierFloorEntry,
    RandomTiebreak,
    Surrender,
    Disconnect,
    IntegrityFailure,
    Timeout,
    SeriesVictory
}

public enum SlCategory
{
    Event,
    Combat
}

public enum IntegrityVerdict
{
    Pending,
    Accepted,
    UnsupportedVersion,
    ModifiedGameFile,
    UnsupportedMod,
    ModifiedModFile,
    ChallengeFailed
}

public enum FriendPresence
{
    Online,
    InRace,
    Offline,
    Request,
    RequestSent,
    SearchResult
}

public enum ActivityProgressState
{
    Locked,
    Available,
    Claimed
}

public enum EntertainmentCoordinationMode
{
    Server,
    SteamP2P
}

public sealed record PlatformIdentity(
    ulong PlatformId,
    string DisplayName,
    byte[]? AvatarRgba = null,
    uint AvatarWidth = 0,
    uint AvatarHeight = 0);

public sealed record RaceInvite(
    string PlayerId,
    string DisplayName,
    string RoomCode = "",
    string PartyId = "",
    QueueKind PartyKind = QueueKind.Casual,
    TeamSize PartyTeamSize = TeamSize.One);

public sealed record RaceParty(
    string Id,
    string LeaderPlayerId,
    QueueKind Kind,
    TeamSize TeamSize,
    IReadOnlyList<RaceParticipant> Members);

public sealed record RaceParticipant(
    string Id,
    string DisplayName,
    string CharacterId,
    bool IsLocal = false,
    bool IsReady = false,
    string Title = "");

public sealed record RaceTeam(
    string Id,
    string Name,
    IReadOnlyList<RaceParticipant> Participants,
    TimeSpan? SharedRunTime = null);

public sealed record RaceRuleSet(
    TeamSize TeamSize,
    string Seed,
    bool RandomSeed,
    int Ascension,
    bool AllowDuplicateCharacters,
    string CharacterPolicy,
    string TimerKind,
    int TimeLimitMinutes,
    string VictoryRule,
    bool AllowSpectators,
    string Visibility,
    IReadOnlyList<string> Modifiers,
    int EventSlLimit = 3,
    int CombatSlLimit = 3,
    string CoordinationMode = "server",
    int BestOf = 1,
    IReadOnlyList<string>? SeriesSeeds = null,
    string SlTimerMode = "continuous",
    int SpectatorSlots = 0);

public sealed record QueueRequest(
    QueueKind Kind,
    TeamSize TeamSize,
    RankedPool? RankedPool,
    RaceRuleSet Rules,
    string? PreferredCharacter = null);

public sealed record RaceResult(
    string MatchId,
    RaceTeam LocalTeam,
    RaceTeam OpponentTeam,
    bool Victory,
    int RatingDelta,
    DateTimeOffset CompletedAt,
    SettlementSnapshot? Settlement = null);

public sealed record ServerClockSnapshot(
    long ServerUnixMilliseconds,
    long MatchStartedUnixMilliseconds,
    long ElapsedMilliseconds,
    long RoundTripMilliseconds,
    bool IsSynchronized,
    bool IsPaused = false);

public sealed record ProgressCheckpoint(
    string MatchId,
    string GameId,
    string TeamId,
    long Sequence,
    int Floor,
    long FloorEnteredAtMilliseconds,
    bool FinalBossDefeated,
    long? CompletedAtMilliseconds,
    ParticipantOutcome Outcome,
    int RestartCount,
    int EventSlUsed,
    int CombatSlUsed);

public sealed record SettlementSide(
    string TeamId,
    string TeamName,
    ParticipantOutcome Outcome,
    int HighestFloor,
    long HighestFloorEnteredAtMilliseconds,
    long? CompletionMilliseconds,
    int RestartCount,
    int EventSlUsed,
    int CombatSlUsed);

public sealed record LegendGameResult(
    int GameNumber,
    string CharacterId,
    string WinnerTeamId,
    FinishReason Reason,
    long ElapsedMilliseconds,
    string GameId = "");

public sealed record LegendDraftState(
    string PlayerOneBanOne,
    string PlayerOneBanTwo,
    string PlayerTwoBanOne,
    string PlayerTwoBanTwo,
    IReadOnlyList<string> UsedCharacters,
    string? SelectedCharacter,
    string? SelectingTeamId,
    int GameNumber,
    int PlayerOneWins,
    int PlayerTwoWins,
    DateTimeOffset? SelectionDeadline);

public sealed record LegendDraftPrompt(
    LegendDraftState State,
    IReadOnlyList<string> AvailableCharacters,
    bool IsBanPhase,
    bool IsLocalSelector);

public sealed record SettlementSnapshot(
    string MatchId,
    string GameId,
    string WinnerTeamId,
    FinishReason Reason,
    SettlementSide Local,
    SettlementSide Opponent,
    int VisibleRatingDelta,
    IReadOnlyList<LegendGameResult> SeriesGames,
    string AuditDetail,
    DateTimeOffset CompletedAt);

public sealed record MatchAssignment(
    string MatchId,
    string GameId,
    string GameVersion,
    QueueKind Kind,
    TeamSize TeamSize,
    RaceRuleSet Rules,
    RaceTeam LocalTeam,
    RaceTeam OpponentTeam,
    string SharedCharacterId,
    string SessionNonce,
    long StartedAtUnixMilliseconds,
    LegendDraftState? LegendDraft = null,
    IReadOnlyDictionary<string, string>? CharacterIds = null,
    string FirstSteamHostPlayerId = "",
    string SecondSteamHostPlayerId = "",
    string FirstSteamLobbyId = "",
    string SecondSteamLobbyId = "");

public sealed record IntegrityFile(string RelativePath, string Sha256, long Size);

public sealed record IntegrityManifest(
    string GameVersion,
    string ManifestVersion,
    IReadOnlyList<IntegrityFile> GameFiles,
    IReadOnlyList<IntegrityFile> AllowedModFiles,
    IReadOnlyList<string> AllowedModIds,
    string Signature);

public sealed record EntertainmentRoomMember(
    string PlayerId,
    string DisplayName,
    int Team,
    bool IsHost,
    bool IsReady = false,
    string CharacterId = "Ironclad");

public sealed record RaceSpectator(
    string PlayerId,
    string DisplayName,
    int WatchingTeam = 1);

public sealed record EntertainmentRoom(
    string Code,
    string HostPlayerId,
    RaceRuleSet Rules,
    IReadOnlyList<EntertainmentRoomMember> Members,
    DateTimeOffset CreatedAt,
    EntertainmentCoordinationMode CoordinationMode = EntertainmentCoordinationMode.Server,
    string State = "waiting",
    IReadOnlyList<RaceSpectator>? Spectators = null);

public sealed record RaceReplaySummary(
    string MatchId,
    string GameId,
    string PlayerId,
    string DisplayName,
    string TeamId,
    string RunId,
    string CharacterId,
    int EventCount,
    bool Completed,
    bool IsLive,
    bool IsPublic,
    DateTimeOffset UpdatedAt);

public sealed record SpectatableRace(
    string MatchId,
    string GameId,
    string PlayerId,
    string DisplayName,
    string CharacterId,
    string Mode,
    bool IsFriend,
    bool IsLegendPublic,
    DateTimeOffset UpdatedAt);

public sealed record QueueSnapshot(
    QueueState State,
    QueueRequest? Request = null,
    RaceTeam? LocalTeam = null,
    RaceTeam? OpponentTeam = null,
    RaceResult? Result = null,
    string Detail = "");

public sealed record RankSnapshot(
    RankedPool Pool,
    string Tier,
    int Division,
    int Points,
    int Wins,
    int Losses,
    int PlacementGamesRemaining,
    int LeaderboardRank);

public sealed record MatchHistoryEntry(
    string MatchId,
    QueueKind Kind,
    TeamSize TeamSize,
    bool Victory,
    TimeSpan RunTime,
    string Character,
    DateTimeOffset PlayedAt,
    int RatingDelta,
    bool Completed = false,
    int HighestFloor = 0,
    TimeSpan OpponentRunTime = default,
    bool OpponentCompleted = false,
    int OpponentHighestFloor = 0,
    IReadOnlyList<string>? OpponentNames = null,
    IReadOnlyList<string>? OpponentCharacters = null,
    IReadOnlyList<LegendGameResult>? SeriesGames = null,
    string LocalTeamId = "");

public sealed record PlayerProfile(
    string Id,
    string DisplayName,
    string EquippedTitle,
    RankSnapshot SoloRank,
    RankSnapshot TeamRank,
    string FavoriteCharacter,
    double WinRate,
    TimeSpan BestTime,
    IReadOnlyList<MatchHistoryEntry> RecentMatches,
    bool IsLocal = false);

public sealed record FriendEntry(
    string Id,
    string DisplayName,
    FriendPresence Presence,
    string Activity,
    string EquippedTitle,
    string RankTier);

public sealed record LeaderboardEntry(
    int Position,
    string PlayerId,
    string DisplayName,
    RankedPool Pool,
    string Tier,
    int Rating,
    double WinRate,
    TimeSpan BestTime,
    bool IsFriend = false);

public sealed record TitleDefinition(
    string Id,
    string Name,
    string Description,
    string UnlockCondition,
    bool IsUnlocked,
    bool IsEquipped = false);

public sealed record ActivityMission(
    string Id,
    string Name,
    string Description,
    int Progress,
    int Goal,
    int RewardPoints,
    ActivityProgressState State);

public sealed record ActivityReward(
    int Level,
    string Name,
    ActivityProgressState State);

public sealed record ActivityDefinition(
    string Id,
    string Name,
    string Description,
    DateTimeOffset EndsAt,
    int CurrentPoints,
    IReadOnlyList<ActivityMission> Missions,
    IReadOnlyList<ActivityReward> Rewards);
