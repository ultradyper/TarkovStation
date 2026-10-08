// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Server.Discord;  // Reserve edit: Better webhook send
using Content.Shared.Administration.Events;
using Content.Shared.CCVar;  // Reserve edit: Better webhook send
using Content.Shared.Database;
using Robust.Shared.Configuration;  // Reserve edit: Better webhook send

namespace Content.Server.Administration.Systems;

public sealed class AdminInfoSystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly IPlayerLocator _locator = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;  // Reserve edit: Better webhook send
    [Dependency] private readonly DiscordWebhook _discord = default!;  // Reserve edit: Better webhook send

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<AdminInfoEvent>(OnAdminInfoEvent);
    }

    private async void OnAdminInfoEvent(AdminInfoEvent ev, EntitySessionEventArgs eventArgs)
    {
        var name = eventArgs.SenderSession.Name;
        if (ev.user == eventArgs.SenderSession.UserId)
            return;

        // Try to get original account for this session
        var main = await _locator.LookupIdAsync(ev.user);

        // We don't have a player like that, ignore.
        if (main == null)
            return;

        var message = $"{name} is attempting to connect with a userid from {main.Username}";  // Reserve edit: Better webhook send

        _adminLog.Add(LogType.AdminMessage, LogImpact.High, $"{name} is attempting to connect with a userid from {main.Username}");
        _chatManager.SendAdminAlert(message);  // Reserve edit: Better webhook send

        await _discord.SendWebhookMessage(message, _cfg.GetCVar(CCVars.DiscordAdminchatWebhook));  // Reserve edit: Better webhook send
    }
}
