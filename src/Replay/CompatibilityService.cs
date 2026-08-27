using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using HarmonyLib;

namespace Sts2SpireRace.Replay;

public static class CompatibilityService
{
    public static ReplayCompatibilityFingerprint Capture()
    {
        List<string> mods = ModManager.GetLoadedMods()
            .Where(m => m.manifest?.id != "sts2_replay")
            .Select(m =>
            {
                string assemblyHash = string.Join("+", LoadedAssemblies(m)
                    .Select(a => a.Location)
                    .Where(File.Exists)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant()));
                return $"{m.manifest?.id ?? "unknown"}@{m.manifest?.version ?? "unknown"}#{assemblyHash}";
            })
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        string joined = string.Join("\n", mods);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
        return new ReplayCompatibilityFingerprint
        {
            GameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "UNRELEASED",
            GitCommit = ReleaseInfoManager.Instance.ReleaseInfo?.Commit ?? "UNKNOWN",
            ModelIdHash = ModelIdSerializationCache.Hash,
            Mods = mods,
            ModFingerprint = hash
        };
    }

    private static IEnumerable<System.Reflection.Assembly> LoadedAssemblies(Mod mod)
    {
        if (AccessTools.Field(typeof(Mod), "assemblies")?.GetValue(mod) is IEnumerable<System.Reflection.Assembly> assemblies)
            return assemblies;
        if (AccessTools.Field(typeof(Mod), "assembly")?.GetValue(mod) is System.Reflection.Assembly assembly)
            return new[] { assembly };
        return Array.Empty<System.Reflection.Assembly>();
    }

    public static bool IsCompatible(ReplayCompatibilityFingerprint recorded, out string reason)
    {
        ReplayCompatibilityFingerprint current = Capture();
        if (recorded.FormatVersion != current.FormatVersion)
        {
            reason = $"Replay format {recorded.FormatVersion} != {current.FormatVersion}";
            return false;
        }
        if (!string.Equals(recorded.GameVersion, current.GameVersion, StringComparison.Ordinal) ||
            !string.Equals(recorded.GitCommit, current.GitCommit, StringComparison.Ordinal))
        {
            reason = $"Game build mismatch ({recorded.GameVersion}/{recorded.GitCommit}).";
            return false;
        }
        if (recorded.ModelIdHash != current.ModelIdHash)
        {
            reason = "Model ID map differs from the recording.";
            return false;
        }
        if (!string.Equals(recorded.ModFingerprint, current.ModFingerprint, StringComparison.Ordinal))
        {
            reason = "Loaded mod set differs from the recording.";
            return false;
        }
        reason = "Compatible";
        return true;
    }
}
