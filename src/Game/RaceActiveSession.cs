using Sts2SpireRace.Core;

namespace Sts2SpireRace.Game;

internal static class RaceActiveSession
{
    private static readonly object Sync = new();
    private static MatchAssignment? _current;

    public static MatchAssignment? Current
    {
        get
        {
            lock (Sync)
                return _current;
        }
    }

    public static void Begin(MatchAssignment assignment)
    {
        lock (Sync)
            _current = assignment;
    }

    public static void Clear(string? gameId = null)
    {
        lock (Sync)
        {
            if (gameId is null || _current?.GameId == gameId)
                _current = null;
        }
    }
}
