using Steamworks;

namespace Sts2SpireRace.Game;

public sealed class SteamWebApiTicketProvider : IDisposable
{
    private Callback<GetTicketForWebApiResponse_t>? _callback;
    private TaskCompletionSource<string>? _pending;
    private HAuthTicket _ticket = HAuthTicket.Invalid;

    public Task<string> GetTicketAsync(CancellationToken cancellationToken = default)
    {
        if (!SteamAPI.IsSteamRunning())
            throw new InvalidOperationException("Steam is not running.");
        if (_pending is not null)
            return _pending.Task;

        _pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _callback = Callback<GetTicketForWebApiResponse_t>.Create(OnTicketReceived);
        _ticket = SteamUser.GetAuthTicketForWebApi("spire-race");
        cancellationToken.Register(() => _pending.TrySetCanceled(cancellationToken));
        return _pending.Task;
    }

    private void OnTicketReceived(GetTicketForWebApiResponse_t response)
    {
        if (response.m_hAuthTicket != _ticket)
            return;
        if (response.m_eResult != EResult.k_EResultOK)
        {
            _pending?.TrySetException(new InvalidOperationException($"Steam ticket failed: {response.m_eResult}"));
            return;
        }
        _pending?.TrySetResult(Convert.ToHexString(response.m_rgubTicket.AsSpan(0, response.m_cubTicket)));
    }

    public void Dispose()
    {
        if (_ticket != HAuthTicket.Invalid)
            SteamUser.CancelAuthTicket(_ticket);
        _callback?.Dispose();
        _callback = null;
        _pending = null;
    }
}
