using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2SpireRace.Game;

internal static class RaceSeriesTransition
{
    public static Task PrepareNextGameAsync(string? expectedGameId = null)
    {
        if (NRun.Instance is null || NGame.Instance is null)
        {
            RaceActiveSession.Clear(expectedGameId);
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Callable.From(() => ReturnToMenuAsync(completion, expectedGameId)).CallDeferred();
        return completion.Task;
    }

    private static async void ReturnToMenuAsync(TaskCompletionSource completion, string? expectedGameId)
    {
        try
        {
            RaceActiveSession.Clear(expectedGameId);
            if (NGame.Instance is not null)
                await NGame.Instance.ReturnToMainMenu();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }
}
