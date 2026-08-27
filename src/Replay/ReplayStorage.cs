using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.IO.Compression;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Map;
using Godot;

namespace Sts2SpireRace.Replay;

public sealed class ReplayStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();

    public string UserRoot => UserDataPathProvider.GetProfileScopedPath(
        SaveManager.Instance.CurrentProfileId,
        "spire_race_replays");

    public string AbsoluteRoot => ProjectSettings.GlobalizePath(UserRoot);

    public string CatalogPath => Path.Combine(AbsoluteRoot, "catalog.json");

    public ReplayStorage()
    {
        Directory.CreateDirectory(AbsoluteRoot);
        Directory.CreateDirectory(Path.Combine(AbsoluteRoot, "runs"));
        Directory.CreateDirectory(Path.Combine(AbsoluteRoot, "branches"));
    }

    public ReplayCatalog LoadCatalog()
    {
        lock (_gate)
        {
            if (!File.Exists(CatalogPath))
            {
                return new ReplayCatalog();
            }
            try
            {
                return JsonSerializer.Deserialize<ReplayCatalog>(File.ReadAllText(CatalogPath), JsonOptions)
                    ?? new ReplayCatalog();
            }
            catch (Exception ex)
            {
                Log.Error($"[SpireRaceReplay] Failed to read catalog: {ex}");
                string broken = CatalogPath + $".broken-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                File.Copy(CatalogPath, broken, overwrite: false);
                return RebuildCatalog();
            }
        }
    }

    public void SaveCatalog(ReplayCatalog catalog)
    {
        lock (_gate)
        {
            AtomicWriteJson(CatalogPath, catalog);
        }
    }

    public string GetRunDirectory(string runId)
    {
        string path = Path.Combine(AbsoluteRoot, "runs", SanitizeId(runId));
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, "combats"));
        Directory.CreateDirectory(Path.Combine(path, "checkpoints"));
        return path;
    }

    public string GetCombatDirectory(string runId)
    {
        return Path.Combine(GetRunDirectory(runId), "combats");
    }

    public string GetBranchDirectory(string branchId)
    {
        string path = Path.Combine(AbsoluteRoot, "branches", SanitizeId(branchId));
        Directory.CreateDirectory(path);
        return path;
    }

    public void SaveRunManifest(RunReplayManifest manifest)
    {
        AtomicWriteJson(Path.Combine(GetRunDirectory(manifest.RunId), "run.json"), manifest);
    }

    public RunReplayManifest LoadRunManifest(string runId)
    {
        string path = Path.Combine(GetRunDirectory(runId), "run.json");
        return JsonSerializer.Deserialize<RunReplayManifest>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Replay manifest is invalid.");
    }

    public byte[] CreateRunBundle(RunReplayManifest manifest)
    {
        string directory = GetRunDirectory(manifest.RunId);
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(AbsoluteRoot, path).Replace('\\', '/');
                ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.Fastest);
                using Stream target = entry.Open();
                using FileStream source = File.Open(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
                source.CopyTo(target);
            }
        }
        return output.ToArray();
    }

    public RunReplayManifest ImportRunBundle(byte[] bundle)
    {
        string root = Path.GetFullPath(AbsoluteRoot) + Path.DirectorySeparatorChar;
        string? manifestPath = null;
        using MemoryStream input = new(bundle, writable: false);
        using ZipArchive archive = new(input, ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            string destination = Path.GetFullPath(Path.Combine(AbsoluteRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Replay archive contains an unsafe path.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string temporary = destination + ".download";
            using (Stream source = entry.Open())
            using (FileStream target = File.Create(temporary)) source.CopyTo(target);
            File.Move(temporary, destination, overwrite: true);
            if (entry.Name.Equals("run.json", StringComparison.OrdinalIgnoreCase)) manifestPath = destination;
        }
        if (manifestPath is null)
        {
            manifestPath = archive.Entries.Select(entry => entry.FullName)
                .FirstOrDefault(path => path.EndsWith("/run.json", StringComparison.OrdinalIgnoreCase));
            if (manifestPath is not null) manifestPath = Path.Combine(AbsoluteRoot, manifestPath.Replace('/', Path.DirectorySeparatorChar));
        }
        if (manifestPath is null || !File.Exists(manifestPath))
            throw new InvalidDataException("Replay archive does not contain run.json.");
        RunReplayManifest manifest = JsonSerializer.Deserialize<RunReplayManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("Replay manifest is invalid.");
        ReplayCatalog catalog = LoadCatalog();
        catalog.Runs.RemoveAll(run => run.RunId == manifest.RunId);
        catalog.Runs.Add(manifest);
        SaveCatalog(catalog);
        return manifest;
    }

    public void SaveTimeline(string absolutePath, ReplayTimeline timeline)
    {
        AtomicWriteJson(absolutePath, timeline);
    }

    public void SaveRunTimeline(string absolutePath, RunReplayTimeline timeline)
    {
        AtomicWriteJson(absolutePath, timeline);
    }

    public void SaveInputStream(string absolutePath, RunReplayInputStream inputStream)
    {
        AtomicWriteJson(absolutePath, inputStream);
    }

    public RunReplayTimeline LoadRunTimeline(string absolutePath)
    {
        return JsonSerializer.Deserialize<RunReplayTimeline>(File.ReadAllText(absolutePath), JsonOptions)
            ?? throw new InvalidDataException("Run timeline JSON was empty.");
    }

    public RunReplayInputStream LoadInputStream(string absolutePath)
    {
        return JsonSerializer.Deserialize<RunReplayInputStream>(File.ReadAllText(absolutePath), JsonOptions)
            ?? throw new InvalidDataException("Run input stream JSON was empty.");
    }

    public void SaveRunCheckpoint(string absolutePath, SerializableRun save)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        string temp = absolutePath + ".tmp";
        File.WriteAllText(temp, JsonSerializationUtility.ToJson(save));
        if (File.Exists(absolutePath)) File.Delete(absolutePath);
        File.Move(temp, absolutePath);
    }

    public SerializableRun LoadRunCheckpoint(string absolutePath)
    {
        ReadSaveResult<SerializableRun> result = JsonSerializationUtility.FromJson<SerializableRun>(File.ReadAllText(absolutePath));
        if (!result.Success) throw new InvalidDataException(result.ErrorMessage ?? "Invalid replay checkpoint.");
        return result.SaveData ?? throw new InvalidDataException("Replay checkpoint contained no run data.");
    }

    public bool ValidateRunData(RunReplayManifest run, out string reason)
    {
        try
        {
            if (string.IsNullOrEmpty(run.InputFile) || string.IsNullOrEmpty(run.TimelineFile))
            {
                reason = "Recording has no operation stream or floor timeline.";
                return false;
            }
            string inputPath = ResolveRelativePath(run.InputFile);
            string timelinePath = ResolveRelativePath(run.TimelineFile);
            if (!File.Exists(inputPath) || !File.Exists(timelinePath))
            {
                reason = "Recording files are missing.";
                return false;
            }
            RunReplayInputStream inputs = LoadInputStream(inputPath);
            RunReplayTimeline timeline = LoadRunTimeline(timelinePath);
            if (timeline.SchemaVersion != 5 || inputs.SchemaVersion != 5)
            {
                reason = $"Replay data schema mismatch (timeline {timeline.SchemaVersion}, inputs {inputs.SchemaVersion}).";
                return false;
            }
            if (timeline.Markers.Count == 0)
            {
                reason = "Recording has no floor boundary checkpoint.";
                return false;
            }
            int previousEvent = -1;
            foreach (RunReplayMarker marker in timeline.Markers)
            {
                if (marker.EventIndex < previousEvent || marker.EventIndex > inputs.Events.Count)
                {
                    reason = $"Floor checkpoint {marker.Index} has an invalid operation position.";
                    return false;
                }
                previousEvent = marker.EventIndex;
                if (string.IsNullOrEmpty(marker.CheckpointFile))
                {
                    reason = $"Floor checkpoint {marker.Index} has no save file.";
                    return false;
                }
                SerializableRun save = LoadRunCheckpoint(ResolveRelativePath(marker.CheckpointFile));
                if (!HasCompleteCurrentMap(save))
                {
                    reason = $"Floor checkpoint {marker.Index} was captured before the original map was complete.";
                    return false;
                }
            }
            reason = "Playable";
            return true;
        }
        catch (Exception ex)
        {
            reason = "Invalid replay data: " + ex.Message;
            return false;
        }
    }

    public static bool HasCompleteCurrentMap(SerializableRun save)
    {
        if (save.CurrentActIndex < 0 || save.CurrentActIndex >= save.Acts.Count) return false;
        SerializableActMap? map = save.Acts[save.CurrentActIndex].SavedMap;
        if (map == null || map.Points.Count == 0 || map.GridWidth <= 0 || map.GridHeight <= 0 ||
            map.StartingPoint == null || map.BossPoint == null)
            return false;
        if (map.StartingPoint.PointType == MapPointType.Unassigned || map.BossPoint.PointType == MapPointType.Unassigned)
            return false;
        if (map.Points.Any(point => point.PointType == MapPointType.Unassigned) || save.VisitedMapCoords.Count == 0)
            return false;
        MapCoord latest = save.VisitedMapCoords[^1];
        return map.Points.Any(point => point.Coord == latest) || map.StartingPoint.Coord == latest || map.BossPoint.Coord == latest;
    }

    public ReplayTimeline LoadTimeline(string absolutePath)
    {
        return JsonSerializer.Deserialize<ReplayTimeline>(File.ReadAllText(absolutePath), JsonOptions)
            ?? throw new InvalidDataException("Timeline JSON was empty.");
    }

    public void SaveBranchManifest(ReplayBranchManifest manifest)
    {
        AtomicWriteJson(Path.Combine(GetBranchDirectory(manifest.BranchId), "branch.json"), manifest);
    }

    public static string ToRelativePath(string root, string absolutePath)
    {
        return Path.GetRelativePath(root, absolutePath).Replace('\\', '/');
    }

    public string ResolveRelativePath(string relativePath)
    {
        string full = Path.GetFullPath(Path.Combine(AbsoluteRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string root = Path.GetFullPath(AbsoluteRoot) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Replay path escaped the replay storage root.");
        }
        return full;
    }

    public void DeleteCombat(CombatReplayManifest combat)
    {
        string replay = ResolveRelativePath(combat.ReplayFile);
        string timeline = ResolveRelativePath(combat.TimelineFile);
        if (File.Exists(replay)) File.Delete(replay);
        if (File.Exists(timeline)) File.Delete(timeline);
        string partial = Path.ChangeExtension(replay, ".partial.mcr");
        if (File.Exists(partial)) File.Delete(partial);
    }

    public void DeleteRun(RunReplayManifest run)
    {
        string target = Path.GetFullPath(Path.Combine(AbsoluteRoot, "runs", SanitizeId(run.RunId)));
        string runsRoot = Path.GetFullPath(Path.Combine(AbsoluteRoot, "runs")) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(runsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run path escaped the replay storage root.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    public void DeleteBranch(ReplayBranchManifest branch)
    {
        string target = Path.GetFullPath(Path.Combine(AbsoluteRoot, "branches", branch.BranchId));
        string branchRoot = Path.GetFullPath(Path.Combine(AbsoluteRoot, "branches")) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(branchRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Branch path escaped the branch storage root.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    private ReplayCatalog RebuildCatalog()
    {
        ReplayCatalog catalog = new();
        string runs = Path.Combine(AbsoluteRoot, "runs");
        if (Directory.Exists(runs))
        {
            foreach (string path in Directory.EnumerateFiles(runs, "run.json", SearchOption.AllDirectories))
            {
                try
                {
                    RunReplayManifest? run = JsonSerializer.Deserialize<RunReplayManifest>(File.ReadAllText(path), JsonOptions);
                    if (run != null) catalog.Runs.Add(run);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[SpireRaceReplay] Ignoring invalid run manifest {path}: {ex.Message}");
                }
            }
        }
        string branches = Path.Combine(AbsoluteRoot, "branches");
        if (Directory.Exists(branches))
        {
            foreach (string path in Directory.EnumerateFiles(branches, "branch.json", SearchOption.AllDirectories))
            {
                try
                {
                    ReplayBranchManifest? branch = JsonSerializer.Deserialize<ReplayBranchManifest>(File.ReadAllText(path), JsonOptions);
                    if (branch != null) catalog.Branches.Add(branch);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[SpireRaceReplay] Ignoring invalid branch manifest {path}: {ex.Message}");
                }
            }
        }
        AtomicWriteJson(CatalogPath, catalog);
        return catalog;
    }

    private static void AtomicWriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
        if (File.Exists(path))
        {
            string backup = path + ".backup";
            File.Replace(temp, path, backup, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    private static string SanitizeId(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
