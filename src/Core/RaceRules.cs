namespace Sts2SpireRace.Core;

public static class RaceRules
{
    public const int MaxAscension = 10;
    public const int CasualMinAscension = 3;
    public const int CasualMaxAscension = 7;
    public const int RankedLowAscension = 7;
    public const int RankedHighAscension = 9;
    public const int MatchTimeLimitMinutes = 180;
    public const int DisconnectGraceSeconds = 10;
    public const int DeathDecisionSeconds = 60;
    public const int LegendPickSeconds = 30;

    public static RankedPool PoolFor(TeamSize size) => size == TeamSize.One ? RankedPool.Solo : RankedPool.Team;

    public static RaceRuleSet CompetitiveDefault(TeamSize size) => new(
        size,
        Seed: string.Empty,
        RandomSeed: true,
        Ascension: CasualMinAscension,
        AllowDuplicateCharacters: false,
        CharacterPolicy: "free_pick",
        TimerKind: "game_time",
        TimeLimitMinutes: MatchTimeLimitMinutes,
        VictoryRule: "shared_team_run_time",
        AllowSpectators: true,
        Visibility: "matchmade",
        Modifiers: Array.Empty<string>(),
        EventSlLimit: 3,
        CombatSlLimit: 3);

    public static RaceRuleSet EntertainmentDefault() => CompetitiveDefault(TeamSize.Two) with
    {
        Seed = "FUN-RACE",
        RandomSeed = true,
        AllowDuplicateCharacters = true,
        TimeLimitMinutes = 180,
        Visibility = "friends",
        Modifiers = ["Draft", "Hoarder"],
        CoordinationMode = "p2p"
    };

    public static int SelectCasualAscension(Random random) =>
        random.Next(CasualMinAscension, CasualMaxAscension + 1);

    public static int SelectRankedAscension(IEnumerable<string> tiers) =>
        tiers.Any(IsHighRank) ? RankedHighAscension : RankedLowAscension;

    public static bool IsHighRank(string? tier) =>
        string.Equals(tier, "Diamond", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(tier, "Legend", StringComparison.OrdinalIgnoreCase);

    public static RaceRuleSet ApplyCompetitiveMode(RaceRuleSet rules, QueueKind kind, IEnumerable<string>? tiers = null, Random? random = null) =>
        kind switch
        {
            QueueKind.Casual => rules with
            {
                Ascension = SelectCasualAscension(random ?? Random.Shared),
                TimeLimitMinutes = MatchTimeLimitMinutes,
                EventSlLimit = 3,
                CombatSlLimit = 3
            },
            QueueKind.Ranked => rules with
            {
                Ascension = SelectRankedAscension(tiers ?? Array.Empty<string>()),
                TimeLimitMinutes = MatchTimeLimitMinutes,
                EventSlLimit = 1,
                CombatSlLimit = 1
            },
            _ => rules
        };

    public static string FormatElapsed(long milliseconds)
    {
        milliseconds = Math.Max(0, milliseconds);
        var value = TimeSpan.FromMilliseconds(milliseconds);
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}"
            : $"{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
    }

    public static void Validate(RaceRuleSet rules)
    {
        if ((int)rules.TeamSize is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(rules.TeamSize));
        if (rules.Ascension is < 0 or > MaxAscension)
            throw new ArgumentOutOfRangeException(nameof(rules.Ascension));
        if (rules.TimeLimitMinutes is < 15 or > 360)
            throw new ArgumentOutOfRangeException(nameof(rules.TimeLimitMinutes));
        if (!rules.RandomSeed && string.IsNullOrWhiteSpace(rules.Seed))
            throw new ArgumentException("A fixed seed cannot be empty.", nameof(rules));
        if (rules.EventSlLimit is < 0 or > 9 || rules.CombatSlLimit is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(rules), "SL limits must be between 0 and 9.");
        if (rules.BestOf is not 1 and not 3)
            throw new ArgumentOutOfRangeException(nameof(rules), "Series length must be BO1 or BO3.");
    }
}
