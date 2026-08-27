using System;
using System.Threading;

namespace Sts2SpireRace.Replay;

public static class ReplayInputGate
{
    private static readonly AsyncLocal<int> InjectionDepth = new();

    public static bool IsInjecting => InjectionDepth.Value > 0;

    public static bool BlockGameplayInput =>
        ReplayMod.Mode == ReplayRuntimeMode.Playback && !IsInjecting;

    public static IDisposable BeginInjection()
    {
        InjectionDepth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            InjectionDepth.Value = Math.Max(0, InjectionDepth.Value - 1);
        }
    }
}
