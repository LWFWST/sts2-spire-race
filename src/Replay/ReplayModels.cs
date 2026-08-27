using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Sts2SpireRace.Replay;

public sealed class ReplayCatalog
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("runs")]
    public List<RunReplayManifest> Runs { get; set; } = new();

    [JsonPropertyName("branches")]
    public List<ReplayBranchManifest> Branches { get; set; } = new();
}

public sealed class RunReplayManifest
{
    [JsonPropertyName("match_id")]
    public string MatchId { get; set; } = "";

    [JsonPropertyName("game_id")]
    public string GameId { get; set; } = "";

    [JsonPropertyName("player_id")]
    public string PlayerId { get; set; } = "";

    [JsonPropertyName("team_id")]
    public string TeamId { get; set; } = "";

    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("seed")]
    public string Seed { get; set; } = "";

    [JsonPropertyName("character")]
    public string Character { get; set; } = "";

    [JsonPropertyName("ascension")]
    public int Ascension { get; set; }

    [JsonPropertyName("game_mode")]
    public string GameMode { get; set; } = "";

    [JsonPropertyName("started_at_unix_ms")]
    public long StartedAtUnixMs { get; set; }

    [JsonPropertyName("combats")]
    public List<CombatReplayManifest> Combats { get; set; } = new();

    [JsonPropertyName("timeline_file")]
    public string TimelineFile { get; set; } = "";

    [JsonPropertyName("input_file")]
    public string InputFile { get; set; } = "";

    [JsonPropertyName("event_count")]
    public int EventCount { get; set; }

    [JsonPropertyName("marker_count")]
    public int MarkerCount { get; set; }

    [JsonPropertyName("ended_at_unix_ms")]
    public long? EndedAtUnixMs { get; set; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "IN_PROGRESS";

    [JsonPropertyName("file_size")]
    public long FileSize { get; set; }

    [JsonPropertyName("compatibility")]
    public ReplayCompatibilityFingerprint Compatibility { get; set; } = new();
}

public sealed class RunReplayTimeline
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 5;

    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("markers")]
    public List<RunReplayMarker> Markers { get; set; } = new();
}

public sealed class RunReplayMarker
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; set; }

    [JsonPropertyName("act")]
    public int Act { get; set; }

    [JsonPropertyName("floor")]
    public int Floor { get; set; }

    [JsonPropertyName("room")]
    public string Room { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("state_hash")]
    public uint StateHash { get; set; }

    [JsonPropertyName("checkpoint_file")]
    public string? CheckpointFile { get; set; }

    [JsonPropertyName("combat_id")]
    public string? CombatId { get; set; }

    [JsonPropertyName("combat_marker")]
    public int? CombatMarker { get; set; }

    [JsonPropertyName("event_index")]
    public int EventIndex { get; set; }

    [JsonPropertyName("next_action_id")]
    public uint NextActionId { get; set; }

    [JsonPropertyName("next_hook_id")]
    public uint NextHookId { get; set; }

    [JsonPropertyName("next_checksum_id")]
    public uint NextChecksumId { get; set; }

    [JsonPropertyName("choice_ids")]
    public List<uint> ChoiceIds { get; set; } = new();

    [JsonPropertyName("reward_ids")]
    public List<int> RewardIds { get; set; } = new();
}

public sealed class RunReplayInputStream
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 5;

    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("events")]
    public List<RunReplayInputEvent> Events { get; set; } = new();
}

public sealed class RunReplayInputEvent
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; set; }

    [JsonPropertyName("operation")]
    public int Operation { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("payload")]
    public string Payload { get; set; } = "";
}

public static class RunReplayInputKinds
{
    public const string Native = "native";
    public const string EventOption = "event_option";
    public const string RestSiteOption = "rest_site_option";
    public const string RewardSelect = "reward_select";
    public const string RewardSkip = "reward_skip";
    public const string MerchantPurchase = "merchant_purchase";
    public const string MerchantCardRemoval = "merchant_card_removal";
    public const string MerchantOpen = "merchant_open";
    public const string MerchantClose = "merchant_close";
    public const string MerchantProceed = "merchant_proceed";
    public const string TreasureOpen = "treasure_open";
    public const string TreasureRelic = "treasure_relic";
    public const string RewardsProceed = "rewards_proceed";
    public const string TreasureProceed = "treasure_proceed";
    public const string AncientDialogue = "ancient_dialogue";
    public const string FakeMerchantProceed = "fake_merchant_proceed";
    public const string CrystalTool = "crystal_tool";
    public const string CrystalCell = "crystal_cell";
    public const string CrystalProceed = "crystal_proceed";
}

public sealed class CombatReplayManifest
{
    [JsonPropertyName("combat_id")]
    public string CombatId { get; set; } = "";

    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("act")]
    public int Act { get; set; }

    [JsonPropertyName("floor")]
    public int Floor { get; set; }

    [JsonPropertyName("encounter")]
    public string Encounter { get; set; } = "";

    [JsonPropertyName("started_at_unix_ms")]
    public long StartedAtUnixMs { get; set; }

    [JsonPropertyName("ended_at_unix_ms")]
    public long? EndedAtUnixMs { get; set; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "IN_PROGRESS";

    [JsonPropertyName("replay_file")]
    public string ReplayFile { get; set; } = "";

    [JsonPropertyName("timeline_file")]
    public string TimelineFile { get; set; } = "";

    [JsonPropertyName("marker_count")]
    public int MarkerCount { get; set; }

    [JsonPropertyName("file_size")]
    public long FileSize { get; set; }

    [JsonPropertyName("compatibility")]
    public ReplayCompatibilityFingerprint Compatibility { get; set; } = new();
}

public sealed class ReplayCompatibilityFingerprint
{
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; } = 5;

    [JsonPropertyName("game_version")]
    public string GameVersion { get; set; } = "UNKNOWN";

    [JsonPropertyName("git_commit")]
    public string GitCommit { get; set; } = "UNKNOWN";

    [JsonPropertyName("model_id_hash")]
    public uint ModelIdHash { get; set; }

    [JsonPropertyName("mod_fingerprint")]
    public string ModFingerprint { get; set; } = "";

    [JsonPropertyName("mods")]
    public List<string> Mods { get; set; } = new();
}

public sealed class ReplayTimeline
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("combat_id")]
    public string CombatId { get; set; } = "";

    [JsonPropertyName("markers")]
    public List<ReplayMarker> Markers { get; set; } = new();
}

public sealed class ReplayMarker
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("event_count")]
    public int EventCount { get; set; }

    [JsonPropertyName("checksum_id")]
    public uint? ChecksumId { get; set; }

    [JsonPropertyName("round")]
    public int Round { get; set; }

    [JsonPropertyName("side")]
    public string Side { get; set; } = "";

    [JsonPropertyName("turn")]
    public int Turn { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("state_hash")]
    public uint StateHash { get; set; }

    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; set; }
}

public sealed class ReplayBranchManifest
{
    [JsonPropertyName("branch_id")]
    public string BranchId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("source_run_id")]
    public string SourceRunId { get; set; } = "";

    [JsonPropertyName("source_combat_id")]
    public string SourceCombatId { get; set; } = "";

    [JsonPropertyName("source_marker")]
    public int SourceMarker { get; set; }

    [JsonPropertyName("created_at_unix_ms")]
    public long CreatedAtUnixMs { get; set; }

    [JsonPropertyName("updated_at_unix_ms")]
    public long UpdatedAtUnixMs { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "ACTIVE";

    [JsonPropertyName("current_run_file")]
    public string CurrentRunFile { get; set; } = "current_run.save";

    [JsonPropertyName("active_combat_file")]
    public string ActiveCombatFile { get; set; } = "active_combat.mcr";

    [JsonPropertyName("in_combat")]
    public bool InCombat { get; set; }

    [JsonPropertyName("active_event_count")]
    public int ActiveEventCount { get; set; }
}

public enum ReplayRuntimeMode
{
    Normal,
    Playback,
    Branch
}
