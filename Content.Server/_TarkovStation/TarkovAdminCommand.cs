// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._TarkovStation;

[Content.Server.Administration.AdminCommand(AdminFlags.Server)]
public sealed partial class TarkovAdminCommand : LocalizedEntityCommands
{
    [Dependency] private TarkovSystem _tarkov = default!;
    public override string Command => "tarkov_admin";
    public override void Execute(IConsoleShell shell, string argStr, string[] args)
        => shell.WriteLine(_tarkov.Admin(args));
}
