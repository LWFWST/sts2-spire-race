using System;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Validation;

namespace Sts2SpireRace.Replay;

public static class RuntimeIsolation
{
    private static readonly MethodInfo? ShouldSaveSetter = AccessTools.PropertySetter(typeof(RunManager), nameof(RunManager.ShouldSave));
    private static readonly MethodInfo? DailyTimeSetter = AccessTools.PropertySetter(typeof(RunManager), nameof(RunManager.DailyTime));
    private static SerializableProgress? _progressSnapshot;

    public static void Apply()
    {
        _progressSnapshot ??= CloneProgress(SaveManager.Instance.Progress.ToSerializable());
        ShouldSaveSetter?.Invoke(RunManager.Instance, new object?[] { false });
        DailyTimeSetter?.Invoke(RunManager.Instance, new object?[] { null });
    }

    public static void Restore()
    {
        if (_progressSnapshot == null) return;
        SaveManager.Instance.Progress = ProgressState.FromSerializable(
            _progressSnapshot,
            new DeserializationContext());
        _progressSnapshot = null;
    }

    private static SerializableProgress CloneProgress(SerializableProgress progress)
    {
        string json = JsonSerializer.Serialize(
            progress,
            JsonSerializationUtility.GetTypeInfo<SerializableProgress>());
        return JsonSerializer.Deserialize(
            json,
            JsonSerializationUtility.GetTypeInfo<SerializableProgress>())
            ?? throw new InvalidOperationException("Could not snapshot the official progress state.");
    }
}
