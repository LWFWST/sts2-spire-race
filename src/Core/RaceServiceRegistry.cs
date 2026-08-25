using Sts2SpireRace.Game;

namespace Sts2SpireRace.Core;

public static class RaceServiceRegistry
{
    private static readonly object Sync = new();
    private static IRaceServices? _services;

    public static IRaceServices Services
    {
        get
        {
            lock (Sync)
                return _services ??= Create();
        }
    }

    private static IRaceServices Create()
    {
        var identity = new SteamIdentityProvider();
        var launcher = new RaceSessionLauncher();
        return RaceRuntimeInfo.DemoMode
            ? new DemoRaceServices(identity, launcher)
            : new RemoteRaceServices(identity, launcher);
    }
}
