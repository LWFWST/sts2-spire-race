using System.Security.Cryptography;

namespace Sts2SpireRace.Core;

public static class RaceAdjudicator
{
    public static (string WinnerTeamId, FinishReason Reason, string AuditDetail) Decide(
        ProgressCheckpoint first,
        ProgressCheckpoint second,
        RandomNumberGenerator? random = null)
    {
        if (IsForcedLoss(first.Outcome) != IsForcedLoss(second.Outcome))
        {
            var winner = IsForcedLoss(first.Outcome) ? second : first;
            return (winner.TeamId, ForcedReason(IsForcedLoss(first.Outcome) ? first.Outcome : second.Outcome), "forced-loss");
        }

        if (first.FinalBossDefeated || second.FinalBossDefeated)
        {
            if (first.FinalBossDefeated != second.FinalBossDefeated)
                return (first.FinalBossDefeated ? first.TeamId : second.TeamId, FinishReason.BossCompletion, "only-finisher");
            var firstTime = first.CompletedAtMilliseconds ?? long.MaxValue;
            var secondTime = second.CompletedAtMilliseconds ?? long.MaxValue;
            if (firstTime != secondTime)
                return (firstTime < secondTime ? first.TeamId : second.TeamId, FinishReason.BossCompletion, "faster-completion");
        }

        if (first.Floor != second.Floor)
            return (first.Floor > second.Floor ? first.TeamId : second.TeamId, FinishReason.HighestFloor, "highest-floor");
        if (first.FloorEnteredAtMilliseconds != second.FloorEnteredAtMilliseconds)
            return (first.FloorEnteredAtMilliseconds < second.FloorEnteredAtMilliseconds ? first.TeamId : second.TeamId,
                FinishReason.EarlierFloorEntry, "earlier-floor-entry");

        random ??= RandomNumberGenerator.Create();
        Span<byte> coin = stackalloc byte[1];
        random.GetBytes(coin);
        return ((coin[0] & 1) == 0 ? first.TeamId : second.TeamId, FinishReason.RandomTiebreak, $"crypto-coin:{coin[0]:X2}");
    }

    public static ProgressCheckpoint RecordFloor(ProgressCheckpoint current, int floor, long enteredAtMilliseconds) =>
        floor > current.Floor
            ? current with { Floor = floor, FloorEnteredAtMilliseconds = enteredAtMilliseconds }
            : current;

    public static ProgressCheckpoint Restart(ProgressCheckpoint current) =>
        current with
        {
            Sequence = current.Sequence + 1,
            Outcome = ParticipantOutcome.Active,
            RestartCount = current.RestartCount + 1,
            FinalBossDefeated = false,
            CompletedAtMilliseconds = null
        };

    public static bool HasSlRemaining(ProgressCheckpoint checkpoint, RaceRuleSet rules, SlCategory category) =>
        category == SlCategory.Combat
            ? checkpoint.CombatSlUsed < rules.CombatSlLimit
            : checkpoint.EventSlUsed < rules.EventSlLimit;

    private static bool IsForcedLoss(ParticipantOutcome outcome) =>
        outcome is ParticipantOutcome.Surrendered or ParticipantOutcome.Forfeited;

    private static FinishReason ForcedReason(ParticipantOutcome outcome) =>
        outcome == ParticipantOutcome.Surrendered ? FinishReason.Surrender : FinishReason.Disconnect;
}
