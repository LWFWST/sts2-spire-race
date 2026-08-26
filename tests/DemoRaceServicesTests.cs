using Sts2SpireRace.Core;
using Xunit;

namespace Sts2SpireRace.Tests;

public sealed class DemoRaceServicesTests
{
    private static DemoRaceServices Create() => new(new StaticIdentityProvider(), demoDelay: TimeSpan.Zero);

    [Theory]
    [InlineData(TeamSize.One, RankedPool.Solo)]
    [InlineData(TeamSize.Two, RankedPool.Team)]
    [InlineData(TeamSize.Three, RankedPool.Team)]
    [InlineData(TeamSize.Four, RankedPool.Team)]
    public void RankedPoolMatchesTeamSemantics(TeamSize size, RankedPool expected) =>
        Assert.Equal(expected, RaceRules.PoolFor(size));

    [Fact]
    public async Task QueueCompletesTheFullDemoStateFlow()
    {
        var service = Create();
        var states = new List<QueueState>();
        service.QueueChanged += snapshot => states.Add(snapshot.State);
        var rules = RaceRules.CompetitiveDefault(TeamSize.Four);

        await service.JoinQueueAsync(new QueueRequest(QueueKind.Ranked, TeamSize.Four, RankedPool.Team, rules));
        Assert.Equal(QueueState.ReadyCheck, service.CurrentQueue.State);
        Assert.Equal(4, service.CurrentQueue.LocalTeam!.Participants.Count);
        Assert.Equal(4, service.CurrentQueue.OpponentTeam!.Participants.Count);

        await service.ConfirmMatchAsync(true);
        Assert.Equal(QueueState.Lobby, service.CurrentQueue.State);
        await service.SetLocalTeamReadyAsync(true);

        Assert.Equal(QueueState.Completed, service.CurrentQueue.State);
        Assert.True(service.CurrentQueue.Result!.Victory);
        Assert.Equal(TimeSpan.FromMinutes(42) + TimeSpan.FromSeconds(17), service.CurrentQueue.Result.LocalTeam.SharedRunTime);
        Assert.Contains(QueueState.Searching, states);
        Assert.Contains(QueueState.MatchFound, states);
        Assert.Contains(QueueState.ReadyCheck, states);
        Assert.Contains(QueueState.Starting, states);
        Assert.Contains(QueueState.Completed, states);
    }

    [Fact]
    public async Task CancelAndDeclineReturnToIdle()
    {
        var service = new DemoRaceServices(new StaticIdentityProvider(), demoDelay: TimeSpan.FromMinutes(1));
        var rules = RaceRules.CompetitiveDefault(TeamSize.One);
        await service.JoinQueueAsync(new QueueRequest(QueueKind.Casual, TeamSize.One, null, rules));
        await service.CancelQueueAsync();
        Assert.Equal(QueueState.Idle, service.CurrentQueue.State);

        var immediate = Create();
        await immediate.JoinQueueAsync(new QueueRequest(QueueKind.Casual, TeamSize.One, null, rules));
        await immediate.ConfirmMatchAsync(false);
        Assert.Equal(QueueState.Idle, immediate.CurrentQueue.State);
        Assert.Equal("declined", immediate.CurrentQueue.Detail);
    }

    [Fact]
    public async Task EquippingTitleIsSessionOnlyAndExclusive()
    {
        var service = Create();
        var titles = await service.GetTitlesAsync();
        var target = titles.First(x => x.IsUnlocked && !x.IsEquipped);
        await service.EquipTitleAsync(target.Id);
        titles = await service.GetTitlesAsync();
        Assert.Single(titles, x => x.IsEquipped);
        Assert.Equal(target.Id, titles.Single(x => x.IsEquipped).Id);
    }

    [Fact]
    public async Task FriendAndActivityMutationsStayInsideDemoService()
    {
        var service = Create();
        var request = (await service.GetFriendsAsync()).First(x => x.Presence == FriendPresence.Request);
        await service.AcceptRequestAsync(request.Id);
        Assert.Equal(FriendPresence.Online, (await service.GetFriendsAsync()).Single(x => x.Id == request.Id).Presence);

        var activity = await service.GetCurrentActivityAsync();
        var claimable = activity.Missions.First(x => x.State == ActivityProgressState.Available);
        await service.ClaimMissionAsync(claimable.Id);
        Assert.Equal(ActivityProgressState.Claimed,
            (await service.GetCurrentActivityAsync()).Missions.Single(x => x.Id == claimable.Id).State);
    }

    [Fact]
    public void EntertainmentRulesValidateFixedSeedAndBounds()
    {
        var defaults = RaceRules.EntertainmentDefault();
        Assert.Equal("p2p", defaults.CoordinationMode);
        Assert.Empty(defaults.Modifiers);
        Assert.True(defaults.AllowDuplicateCharacters);
        Assert.Equal("host_for_1v1", defaults.CharacterPolicy);
        Assert.Equal("certified_race", defaults.VictoryRule);
        RaceRules.Validate(RaceRules.EntertainmentDefault());
        Assert.Throws<ArgumentException>(() => RaceRules.Validate(RaceRules.EntertainmentDefault() with
        {
            RandomSeed = false,
            Seed = ""
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => RaceRules.Validate(RaceRules.EntertainmentDefault() with
        {
            Ascension = RaceRules.MaxAscension + 1
        }));
        RaceRules.Validate(RaceRules.EntertainmentDefault() with { Ascension = RaceRules.MaxAscension });
        RaceRules.Validate(RaceRules.EntertainmentDefault() with { BestOf = 3 });
        var customSeries = RaceRules.NormalizeEntertainment(RaceRules.EntertainmentDefault() with
        {
            BestOf = 3,
            RandomSeed = false,
            Seed = "",
            SeriesSeeds = ["FIRST", "", "THIRD"],
            AllowDuplicateCharacters = false,
            CharacterPolicy = "random_pick",
            VictoryRule = "custom"
        });
        RaceRules.Validate(customSeries);
        Assert.Equal(["FIRST", "", "THIRD"], customSeries.SeriesSeeds);
        Assert.True(customSeries.AllowDuplicateCharacters);
        Assert.Equal("host_for_1v1", customSeries.CharacterPolicy);
        Assert.Throws<ArgumentOutOfRangeException>(() => RaceRules.Validate(RaceRules.EntertainmentDefault() with { BestOf = 2 }));
    }

    private sealed class StaticIdentityProvider : IRacePlatformIdentityProvider
    {
        public Task<PlatformIdentity> GetLocalIdentityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlatformIdentity(42, "Local Racer"));
    }
}
