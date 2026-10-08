// SPDX-FileCopyrightText: 2025 J <billsmith116@gmail.com>
// SPDX-FileCopyrightText: 2025 Kirill <kirill@example.com>
// SPDX-FileCopyrightText: 2025 ReserveBot <211949879+ReserveBot@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 slarticodefast <161409025+slarticodefast@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.GameTicking;
using Content.Server.Ghost;
using Content.Shared.Administration;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Robust.Server.Player;
using Robust.Shared.Console;

namespace Content.Server.Administration.Commands;

[AdminCommand(AdminFlags.Admin)]
public sealed class ForceGhostCommand : LocalizedEntityCommands
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly GhostSystem _ghost = default!;

    public override string Command => "forceghost";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length == 0 || args.Length > 1)
        {
            shell.WriteError(LocalizationManager.GetString("shell-wrong-arguments-number"));
            return;
        }

        if (!_playerManager.TryGetSessionByUsername(args[0], out var player))
        {
            shell.WriteError(LocalizationManager.GetString("shell-target-player-does-not-exist"));
            return;
        }

        if (!_gameTicker.PlayerGameStatuses.TryGetValue(player.UserId, out var playerStatus) ||
            playerStatus is not PlayerGameStatus.JoinedGame)
        {
            shell.WriteLine(Loc.GetString("cmd-forceghost-error-lobby"));
            return;
        }

        if (!_mind.TryGetMind(player, out var mindId, out var mind))
            (mindId, mind) = _mind.CreateMind(player.UserId);

        // Reserve-Start
        if (!_ghost.OnGhostAttempt(mindId, false, viaCommand: true, forced: true, mind: mind))
            shell.WriteLine(Loc.GetString("cmd-forceghost-denied"));
        // Reserve-End
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            return CompletionResult.FromHintOptions(
                CompletionHelper.SessionNames(players: _playerManager),
                Loc.GetString("cmd-forceghost-hint"));
        }

        return CompletionResult.Empty;
    }
}
