using System.Reflection;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2SpireRace.Replay;

internal static class ReplayVersionCompat
{
    private static readonly MethodInfo SetUpReplayMethod = typeof(RunManager).GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(method => method.Name == nameof(RunManager.SetUpReplay))
        .OrderByDescending(method => method.GetParameters().Length)
        .First(method => method.GetParameters().Length is 2 or 3);

    public static void SetUpReplay(RunState state, CombatReplay replay, ulong playerId)
    {
        var arguments = SetUpReplayMethod.GetParameters().Length == 3
            ? new object?[] { state, replay, playerId }
            : new object?[] { state, replay };
        SetUpReplayMethod.Invoke(RunManager.Instance, arguments);
    }
}
