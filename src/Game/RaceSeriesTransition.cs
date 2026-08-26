using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2SpireRace.Game;

internal static class RaceSeriesTransition
{
    public static Task PrepareNextGameAsync()
    {
        if (NRun.Instance is null || NGame.Instance is null)
        {
            RaceActiveSession.Clear();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Callable.From(() => ReturnToMenuAsync(completion)).CallDeferred();
        return completion.Task;
    }

    private static async void ReturnToMenuAsync(TaskCompletionSource completion)
    {
        try
        {
            RaceActiveSession.Clear();
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
