namespace Sts2SpireRace.Core;

public static class RaceRating
{
    public const double InitialElo = 1500;

    public static double Expected(double rating, double opponentRating) =>
        1d / (1d + Math.Pow(10d, (opponentRating - rating) / 400d));

    public static int HiddenDelta(double rating, double opponentRating, bool won, int gamesPlayed, bool legend)
    {
        var k = gamesPlayed < 10 ? 48 : legend ? 16 : 24;
        return (int)Math.Round(k * ((won ? 1d : 0d) - Expected(rating, opponentRating)), MidpointRounding.AwayFromZero);
    }

    public static int VisibleDelta(double hiddenRating, double opponentHiddenRating, bool won)
    {
        var adjustment = (int)Math.Round((0.5d - Expected(hiddenRating, opponentHiddenRating)) * 10d, MidpointRounding.AwayFromZero);
        adjustment = Math.Clamp(adjustment, -5, 5);
        return (won ? 25 : -20) + adjustment;
    }
}
