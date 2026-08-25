namespace Sts2SpireRace.Core;

public interface IRaceMatchmakingService
{
    QueueSnapshot CurrentQueue { get; }
    event Action<QueueSnapshot>? QueueChanged;
    Task JoinQueueAsync(QueueRequest request, CancellationToken cancellationToken = default);
    Task CancelQueueAsync(CancellationToken cancellationToken = default);
    Task ConfirmMatchAsync(bool accepted, CancellationToken cancellationToken = default);
    Task SetLocalTeamReadyAsync(bool ready, CancellationToken cancellationToken = default);
}

public interface IRaceSessionLauncher
{
    Task LaunchAsync(QueueRequest request, RaceTeam localTeam, RaceTeam opponentTeam, CancellationToken cancellationToken = default);
    Task LaunchAsync(MatchAssignment assignment, CancellationToken cancellationToken = default) =>
        LaunchAsync(new QueueRequest(assignment.Kind, assignment.TeamSize, RaceRules.PoolFor(assignment.TeamSize), assignment.Rules), assignment.LocalTeam, assignment.OpponentTeam, cancellationToken);
}

public interface IRaceSteamLobbyCoordinator
{
    Task<ulong> CreateTeamLobbyAsync(MatchAssignment assignment, CancellationToken cancellationToken = default);
}

public interface IRaceEntertainmentP2PLauncher
{
    Task LaunchDirectHostAsync(RaceRuleSet rules, CancellationToken cancellationToken = default);
}

public interface IRaceAuthService
{
    bool IsAuthenticated { get; }
    Task AuthenticateAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
}

public interface IRaceClockService
{
    ServerClockSnapshot CurrentClock { get; }
    event Action<ServerClockSnapshot>? ClockChanged;
    Task SynchronizeAsync(CancellationToken cancellationToken = default);
}

public interface IRaceMatchService
{
    MatchAssignment? CurrentMatch { get; }
    SettlementSnapshot? CurrentSettlement { get; }
    LegendDraftPrompt? CurrentLegendDraft { get; }
    event Action<MatchAssignment?>? MatchChanged;
    event Action<SettlementSnapshot>? MatchSettled;
    event Action<LegendDraftPrompt?>? LegendDraftChanged;
    Task ReportProgressAsync(ProgressCheckpoint checkpoint, string idempotencyKey, CancellationToken cancellationToken = default);
    Task ChooseDeathActionAsync(bool restart, CancellationToken cancellationToken = default);
    Task RequestSaveAndQuitAsync(SlCategory category, bool confirmForfeit, CancellationToken cancellationToken = default);
    Task ResumeSavedRunAsync(string idempotencyKey, CancellationToken cancellationToken = default);
    Task SurrenderAsync(CancellationToken cancellationToken = default);
    Task VoteSurrenderAsync(bool accept, CancellationToken cancellationToken = default);
    Task SubmitLegendBansAsync(string banOne, string banTwo, CancellationToken cancellationToken = default);
    Task SelectLegendCharacterAsync(string characterId, CancellationToken cancellationToken = default);
}

public interface IRaceIntegrityService
{
    IntegrityVerdict LastVerdict { get; }
    Task<IntegrityVerdict> VerifyAsync(string gameVersion, CancellationToken cancellationToken = default);
}

public interface IRaceEntertainmentRoomService
{
    EntertainmentRoom? CurrentRoom { get; }
    event Action<EntertainmentRoom?>? RoomChanged;
    event Action<string>? RoomExited;
    Task<EntertainmentRoom> CreateRoomAsync(RaceRuleSet rules, CancellationToken cancellationToken = default);
    Task<EntertainmentRoom> JoinRoomAsync(string code, CancellationToken cancellationToken = default);
    Task<EntertainmentRoom> UpdateRoomRulesAsync(RaceRuleSet rules, CancellationToken cancellationToken = default);
    Task<EntertainmentRoom> SwitchTeamAsync(CancellationToken cancellationToken = default);
    Task<EntertainmentRoom> SetRoomMemberAsync(string characterId, bool ready, CancellationToken cancellationToken = default);
    Task<EntertainmentRoom> StartRoomAsync(CancellationToken cancellationToken = default);
    Task LeaveRoomAsync(CancellationToken cancellationToken = default);
    Task InviteFriendAsync(string playerId, CancellationToken cancellationToken = default);
}

public interface IRacePartyService
{
    RaceParty? CurrentParty { get; }
    event Action<RaceParty?>? PartyChanged;
    Task OpenPartyLobbyAsync(QueueKind kind, TeamSize teamSize, CancellationToken cancellationToken = default);
    Task SetPartyCharacterAsync(string characterId, CancellationToken cancellationToken = default);
    Task LeavePartyAsync(CancellationToken cancellationToken = default);
}

public interface IRaceProfileService
{
    Task<PlayerProfile> GetLocalProfileAsync(CancellationToken cancellationToken = default);
    Task<PlayerProfile?> GetProfileAsync(string playerId, CancellationToken cancellationToken = default);
    Task<PlayerProfile> UpdateLocalProfileAsync(string displayName, string favoriteCharacter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TitleDefinition>> GetTitlesAsync(CancellationToken cancellationToken = default);
    Task EquipTitleAsync(string titleId, CancellationToken cancellationToken = default);
}

public interface IRaceSocialService
{
    event Action<RaceInvite>? InviteReceived;
    Task<IReadOnlyList<FriendEntry>> GetFriendsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FriendEntry>> SearchPlayersAsync(string query, CancellationToken cancellationToken = default);
    Task SendFriendRequestAsync(string playerId, CancellationToken cancellationToken = default);
    Task AcceptRequestAsync(string playerId, CancellationToken cancellationToken = default);
    Task DeclineRequestAsync(string playerId, CancellationToken cancellationToken = default);
    Task RemoveFriendAsync(string playerId, CancellationToken cancellationToken = default);
    Task InviteAsync(string playerId, CancellationToken cancellationToken = default);
    Task RespondToInviteAsync(string playerId, bool accepted, CancellationToken cancellationToken = default);
}

public interface IRaceLeaderboardService
{
    Task<IReadOnlyList<LeaderboardEntry>> QueryAsync(
        RankedPool pool,
        bool friendsOnly,
        bool historicalSeason,
        CancellationToken cancellationToken = default);
}

public interface IRaceActivityService
{
    Task<ActivityDefinition> GetCurrentActivityAsync(CancellationToken cancellationToken = default);
    Task ClaimMissionAsync(string missionId, CancellationToken cancellationToken = default);
    Task ClaimRewardAsync(int level, CancellationToken cancellationToken = default);
}

public interface IRacePlatformIdentityProvider
{
    Task<PlatformIdentity> GetLocalIdentityAsync(CancellationToken cancellationToken = default);
}

public interface IRaceServices :
    IRaceAuthService,
    IRaceMatchmakingService,
    IRaceProfileService,
    IRaceSocialService,
    IRaceLeaderboardService,
    IRaceActivityService
{
    IRacePlatformIdentityProvider IdentityProvider { get; }
    IRaceSessionLauncher SessionLauncher { get; }
    Task ChangeServerAsync(Uri serverUri, CancellationToken cancellationToken = default);
}
