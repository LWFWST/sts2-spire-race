using System.Security.Cryptography;
using Sts2SpireRace.Core;
using Xunit;

namespace Sts2SpireRace.Tests;

public sealed class RaceCompetitiveCoreTests
{
    [Fact]
    public void FinalBossAlwaysBeatsHigherFailedFloor()
    {
        var finisher = Progress("finish", 30, 40_000) with { FinalBossDefeated = true, CompletedAtMilliseconds = 90_123, Outcome = ParticipantOutcome.Finished };
        var failed = Progress("failed", 50, 30_000) with { Outcome = ParticipantOutcome.ScoreLocked };
        var result = RaceAdjudicator.Decide(finisher, failed);
        Assert.Equal("finish", result.WinnerTeamId);
        Assert.Equal(FinishReason.BossCompletion, result.Reason);
    }

    [Fact]
    public void FailedRunsUseHighestFloorThenFirstEntry()
    {
        var high = RaceAdjudicator.Decide(Progress("a", 42, 20_000), Progress("b", 41, 10_000));
        Assert.Equal(("a", FinishReason.HighestFloor), (high.WinnerTeamId, high.Reason));
        var early = RaceAdjudicator.Decide(Progress("a", 42, 20_000), Progress("b", 42, 21_000));
        Assert.Equal(("a", FinishReason.EarlierFloorEntry), (early.WinnerTeamId, early.Reason));
    }

    [Fact]
    public void RestartPreservesHighScoreAndClockEntry()
    {
        var checkpoint = Progress("a", 39, 123_456) with { Sequence = 8, RestartCount = 1, Outcome = ParticipantOutcome.ScoreLocked };
        var restarted = RaceAdjudicator.Restart(checkpoint);
        Assert.Equal(39, restarted.Floor);
        Assert.Equal(123_456, restarted.FloorEnteredAtMilliseconds);
        Assert.Equal(2, restarted.RestartCount);
        Assert.Equal(ParticipantOutcome.Active, restarted.Outcome);
        Assert.Equal(39, RaceAdjudicator.RecordFloor(restarted, 12, 200_000).Floor);
    }

    [Fact]
    public void CompetitiveAscensionsAndSlBudgetsMatchSpecification()
    {
        var random = new Random(42);
        Assert.All(Enumerable.Range(0, 100).Select(_ => RaceRules.SelectCasualAscension(random)), value => Assert.InRange(value, 3, 7));
        Assert.Equal(7, RaceRules.SelectRankedAscension(["Bronze", "Platinum"]));
        Assert.Equal(9, RaceRules.SelectRankedAscension(["Gold", "Diamond"]));
        var casual = RaceRules.ApplyCompetitiveMode(RaceRules.CompetitiveDefault(TeamSize.One), QueueKind.Casual, random: random);
        var ranked = RaceRules.ApplyCompetitiveMode(RaceRules.CompetitiveDefault(TeamSize.Four), QueueKind.Ranked, ["Legend"]);
        Assert.Equal((3, 3, 180), (casual.EventSlLimit, casual.CombatSlLimit, casual.TimeLimitMinutes));
        Assert.Equal((1, 1, 9), (ranked.EventSlLimit, ranked.CombatSlLimit, ranked.Ascension));
    }

    [Theory]
    [InlineData(0, "00:00.000")]
    [InlineData(62_345, "01:02.345")]
    [InlineData(3_661_007, "01:01:01.007")]
    public void MillisecondTimerFormattingIsStable(long value, string expected) =>
        Assert.Equal(expected, RaceRules.FormatElapsed(value));

    [Fact]
    public void EntertainmentTimerAndSpectatorRulesAreNormalized()
    {
        var server = RaceRules.NormalizeEntertainment(RaceRules.EntertainmentDefault() with
        {
            CoordinationMode = "server",
            SlTimerMode = "pause_on_save",
            SpectatorSlots = 12
        });
        Assert.Equal("pause_on_save", server.SlTimerMode);
        Assert.Equal(8, server.SpectatorSlots);
        Assert.True(server.AllowSpectators);

        var p2p = RaceRules.NormalizeEntertainment(server with { CoordinationMode = "p2p" });
        Assert.Equal(0, p2p.SpectatorSlots);
        Assert.False(p2p.AllowSpectators);
    }

    [Fact]
    public void EloAndVisibleRatingUseLockedConstants()
    {
        Assert.Equal(24, RaceRating.HiddenDelta(1500, 1500, true, 0, false));
        Assert.Equal(12, RaceRating.HiddenDelta(1500, 1500, true, 20, false));
        Assert.Equal(8, RaceRating.HiddenDelta(1500, 1500, true, 20, true));
        Assert.Equal(25, RaceRating.VisibleDelta(1500, 1500, true));
        Assert.Equal(-20, RaceRating.VisibleDelta(1500, 1500, false));
    }

    private static ProgressCheckpoint Progress(string team, int floor, long entered) =>
        new("m", "g", team, 1, floor, entered, false, null, ParticipantOutcome.Active, 0, 0, 0);
}
