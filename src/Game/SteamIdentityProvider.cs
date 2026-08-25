using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Steam;
using Steamworks;
using Sts2SpireRace.Core;

namespace Sts2SpireRace.Game;

public sealed class SteamIdentityProvider : IRacePlatformIdentityProvider
{
    public Task<PlatformIdentity> GetLocalIdentityAsync(CancellationToken cancellationToken = default)
    {
        if (RaceRuntimeInfo.DevelopmentAuthentication && RaceRuntimeInfo.DevelopmentPlatformId is ulong developmentId)
            return Task.FromResult(new PlatformIdentity(developmentId,
                RaceRuntimeInfo.DevelopmentDisplayName ?? $"Race Tester {developmentId % 100}"));
        try
        {
            if (!SteamInitializer.Initialized)
                return Task.FromResult(new PlatformIdentity(0, "Spire Racer"));

            var id = SteamUser.GetSteamID().m_SteamID;
            var name = PlatformUtil.GetPlayerNameRaw(PlatformType.Steam, id);
            var avatarHandle = SteamFriends.GetLargeFriendAvatar(new CSteamID(id));
            if (avatarHandle > 0 && SteamUtils.GetImageSize(avatarHandle, out var width, out var height))
            {
                var rgba = new byte[checked(width * height * 4)];
                if (SteamUtils.GetImageRGBA(avatarHandle, rgba, rgba.Length))
                    return Task.FromResult(new PlatformIdentity(id, name, rgba, width, height));
            }
            return Task.FromResult(new PlatformIdentity(id, name));
        }
        catch
        {
            return Task.FromResult(new PlatformIdentity(0, "Spire Racer"));
        }
    }
}
