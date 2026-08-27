using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2SpireRace.Replay;

public static class NativeReplayAdapter
{
    private static readonly FieldInfo ReplayField = AccessTools.Field(typeof(CombatReplayWriter), "_replay")
        ?? throw new MissingFieldException(typeof(CombatReplayWriter).FullName, "_replay");

    private static readonly FieldInfo ReplayChecksumsField = AccessTools.Field(typeof(ChecksumTracker), "_replayChecksums")
        ?? throw new MissingFieldException(typeof(ChecksumTracker).FullName, "_replayChecksums");

    public static CombatReplay? GetCurrentReplay()
    {
        CombatReplayWriter? writer = RunManager.Instance.CombatReplayWriter;
        return writer == null ? null : ReplayField.GetValue(writer) as CombatReplay;
    }

    public static int GetEventCount()
    {
        return GetCurrentReplay()?.events.Count ?? 0;
    }

    public static uint? GetLastChecksumId()
    {
        CombatReplay? replay = GetCurrentReplay();
        return replay?.checksumData.Count > 0 ? replay.checksumData[^1].checksumData.id : null;
    }

    public static void FlushCurrent(string absolutePath)
    {
        CombatReplayWriter? writer = RunManager.Instance.CombatReplayWriter;
        if (writer == null || !writer.IsRecordingReplay)
        {
            return;
        }
        string godotPath = absolutePath.Replace('\\', '/');
        writer.WriteReplay(godotPath, stopRecording: false);
    }

    public static CombatReplay ReadReplay(string absolutePath)
    {
        byte[] bytes = File.ReadAllBytes(absolutePath);
        PacketReader reader = new();
        reader.Reset(bytes);
        return reader.Read<CombatReplay>();
    }

    public static void WriteReplay(string absolutePath, CombatReplay replay)
    {
        PacketWriter writer = new();
        writer.Write(replay);
        writer.ZeroByteRemainder();
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        string temp = absolutePath + ".tmp";
        File.WriteAllBytes(temp, writer.Buffer.AsSpan(0, writer.BytePosition).ToArray());
        if (File.Exists(absolutePath)) File.Delete(absolutePath);
        File.Move(temp, absolutePath);
    }

    public static string SerializeEvent(CombatReplayEvent replayEvent)
    {
        PacketWriter writer = new();
        writer.Write(replayEvent);
        writer.ZeroByteRemainder();
        return Convert.ToBase64String(writer.Buffer.AsSpan(0, writer.BytePosition));
    }

    public static CombatReplayEvent DeserializeEvent(string payload)
    {
        PacketReader reader = new();
        reader.Reset(Convert.FromBase64String(payload));
        return reader.Read<CombatReplayEvent>();
    }

    public static CombatReplayEvent FromGameAction(GameAction action)
    {
        if (action is GenericHookGameAction hook)
        {
            return new CombatReplayEvent
            {
                playerId = action.OwnerId,
                eventType = CombatReplayEventType.HookAction,
                hookId = hook.HookId,
                gameActionType = hook.ActionType
            };
        }
        if (!action.RecordableToReplay)
            throw new InvalidOperationException($"Unrecordable game action: {action}");
        return new CombatReplayEvent
        {
            playerId = action.OwnerId,
            eventType = CombatReplayEventType.GameAction,
            action = action.ToNetAction()
        };
    }

    public static CombatReplay CreateReplayShell(SerializableRun save, RunReplayMarker marker)
    {
        return new CombatReplay
        {
            version = CompatibilityService.Capture().GameVersion,
            gitCommit = CompatibilityService.Capture().GitCommit,
            modelIdHash = CompatibilityService.Capture().ModelIdHash,
            choiceIds = marker.ChoiceIds.ToList(),
            rewardIds = marker.RewardIds.ToList(),
            nextActionId = marker.NextActionId,
            nextChecksumId = marker.NextChecksumId,
            nextHookId = marker.NextHookId,
            serializableRun = save
        };
    }

    public static uint CalculateCurrentStateHash()
    {
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (state == null || RunManager.Instance.ChecksumTracker == null)
        {
            return 0;
        }
        NetFullCombatState fullState = NetFullCombatState.FromRun(state, RunManager.Instance.ActionExecutor?.CurrentlyRunningAction);
        return RunManager.Instance.ChecksumTracker.GenerateChecksum(fullState);
    }

    public static void DisableReplayChecksumComparison()
    {
        if (RunManager.Instance.ChecksumTracker != null)
        {
            ReplayChecksumsField.SetValue(RunManager.Instance.ChecksumTracker, null);
        }
    }

    public static CombatReplay PrefixThroughMarker(CombatReplay source, ReplayMarker marker)
    {
        return new CombatReplay
        {
            version = source.version,
            gitCommit = source.gitCommit,
            modelIdHash = source.modelIdHash,
            choiceIds = source.choiceIds.ToList(),
            rewardIds = source.rewardIds.ToList(),
            nextActionId = source.nextActionId,
            nextChecksumId = source.nextChecksumId,
            nextHookId = source.nextHookId,
            serializableRun = source.serializableRun,
            events = source.events.Take(marker.EventCount).ToList(),
            checksumData = source.checksumData
                .Where(c => !marker.ChecksumId.HasValue || c.checksumData.id <= marker.ChecksumId.Value)
                .ToList()
        };
    }
}
