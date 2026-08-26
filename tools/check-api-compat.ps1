param(
    [string]$Source107 = "C:\CP\MCC2\Slay the Spire 2 107.1",
    [string]$Source111 = "C:\CP\MCC2\Slay the Spire 2"
)

$ErrorActionPreference = "Stop"
$checks = @(
    @{ File = "src\Core\Nodes\Screens\MainMenu\NMainMenu.cs"; Pattern = "public override void _Ready\(\)"; Name = "NMainMenu._Ready" },
    @{ File = "src\Core\Nodes\Screens\MainMenu\NMainMenu.cs"; Pattern = "public NMainMenuSubmenuStack SubmenuStack"; Name = "NMainMenu.SubmenuStack" },
    @{ File = "src\Core\Nodes\Screens\MainMenu\NSubmenuStack.cs"; Pattern = "public void Push\(NSubmenu screen\)"; Name = "NSubmenuStack.Push" },
    @{ File = "src\Core\Nodes\Screens\MainMenu\NSubmenuStack.cs"; Pattern = "public void Pop\(\)"; Name = "NSubmenuStack.Pop" },
    @{ File = "src\Core\Nodes\Screens\MainMenu\NSubmenu.cs"; Pattern = "protected virtual void ConnectSignals\(\)"; Name = "NSubmenu.ConnectSignals" },
    @{ File = "src\Core\Nodes\Screens\MainMenu\NMainMenuTextButton.cs"; Pattern = "public partial class NMainMenuTextButton"; Name = "NMainMenuTextButton" },
    @{ File = "src\Core\Platform\PlatformUtil.cs"; Pattern = "public static string GetPlayerNameRaw"; Name = "PlatformUtil.GetPlayerNameRaw" },
    @{ File = "src\Core\Platform\PlatformUtil.cs"; Pattern = "public static ulong GetLocalPlayerId"; Name = "PlatformUtil.GetLocalPlayerId" },
    @{ File = "src\Core\Localization\LocString.cs"; Pattern = "public static void SubscribeToLocaleChange"; Name = "LocString.SubscribeToLocaleChange" },
    @{ File = "src\Core\Multiplayer\NetHostGameService.cs"; Pattern = "StartSteamHost\(int maxClients\)"; Name = "NetHostGameService.StartSteamHost" },
    @{ File = "src\Core\Multiplayer\Connection\SteamClientConnectionInitializer.cs"; Pattern = "FromLobby\(ulong lobbySteamId\)"; Name = "SteamClientConnectionInitializer.FromLobby" },
    @{ File = "src\Core\Multiplayer\Game\JoinFlow.cs"; Pattern = "Task<JoinResult> Begin\("; Name = "JoinFlow.Begin" },
    @{ File = "src\Core\Nodes\Screens\CharacterSelect\NCharacterSelectScreen.cs"; Pattern = "InitializeMultiplayerAsHost\("; Name = "NCharacterSelectScreen.InitializeMultiplayerAsHost" },
    @{ File = "src\Core\Nodes\Screens\CharacterSelect\NCharacterSelectScreen.cs"; Pattern = "InitializeMultiplayerAsClient\("; Name = "NCharacterSelectScreen.InitializeMultiplayerAsClient" },
    @{ File = "src\Core\Nodes\Screens\CustomRun\NCustomRunScreen.cs"; Pattern = "InitializeMultiplayerAsHost\("; Name = "NCustomRunScreen.InitializeMultiplayerAsHost" },
    @{ File = "src\Core\Nodes\Screens\CustomRun\NCustomRunScreen.cs"; Pattern = "InitializeMultiplayerAsClient\("; Name = "NCustomRunScreen.InitializeMultiplayerAsClient" },
    @{ File = "src\Core\Platform\Steam\SteamJoinCallbackHandler.cs"; Pattern = "OnSteamLobbyJoinRequested\(GameLobbyJoinRequested_t lobbyJoinRequest\)"; Name = "SteamJoinCallbackHandler.OnSteamLobbyJoinRequested" },
    @{ File = "src\Core\Nodes\Debug\NDevConsole.cs"; Pattern = "public override void _Input\(InputEvent inputEvent\)"; Name = "NDevConsole._Input" },
    @{ File = "src\Core\Nodes\Debug\NDevConsole.cs"; Pattern = "public void HideConsole\(\)"; Name = "NDevConsole.HideConsole" }
)

$assets = @(
    "scenes\ui\submenu_button.tscn",
    "scenes\ui\back_button.tscn",
    "images\ui\tiny_nine_patch.png",
    "images\packed\common_ui\submenu_panel_short.png",
    "images\ui\reward_screen\reward_skip_button.png",
    "images\ui\main_menu\submenu_standard.png",
    "images\ui\main_menu\submenu_daily.png",
    "images\ui\main_menu\submenu_custom.png",
    "images\ui\main_menu\submenu_join.png",
    "images\packed\main_menu\submenu_stats_icon.png",
    "images\packed\main_menu\submenu_leaderboards_icon.png",
    "images\packed\main_menu\submenu_trophy_icon.png",
    "themes\kreon_regular_shared.tres",
    "themes\kreon_bold_glyph_space_two.tres"
)

$failures = @()
foreach ($version in @(@{ Label = "v0.107.1"; Root = $Source107 }, @{ Label = "v0.111.0"; Root = $Source111 })) {
    foreach ($check in $checks) {
        $path = Join-Path $version.Root $check.File
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or -not (Select-String -LiteralPath $path -Pattern $check.Pattern -Quiet)) {
            $failures += "$($version.Label): missing API $($check.Name)"
        }
    }
    foreach ($asset in $assets) {
        if (-not (Test-Path -LiteralPath (Join-Path $version.Root $asset) -PathType Leaf)) {
            $failures += "$($version.Label): missing resource $asset"
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Compatibility check passed for v0.107.1 and v0.111.0: $($checks.Count) APIs and $($assets.Count) resources."
