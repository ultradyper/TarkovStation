// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using Content.Shared._TarkovStation;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Localization;

namespace Content.Client._TarkovStation.UI;

public sealed partial class TarkovServiceWindow
{
    private readonly Label _departure = TarkovTheme.Label("", true);
    private readonly Label _queueDetails = TarkovTheme.Label("");
    private readonly Label _queueNotice = TarkovTheme.Label("");
    private readonly Label _conditionsTitle = TarkovTheme.Label("", true);
    private readonly RichTextLabel _conditions = TarkovTheme.Paragraph("");
    private readonly RichTextLabel _mapBriefing = TarkovTheme.Paragraph("");
    private readonly RichTextLabel _eventBriefing = TarkovTheme.Paragraph("");
    private readonly RichTextLabel _activeRaid = TarkovTheme.Paragraph("");
    private TarkovButton? _readyButton;
    private TarkovButton? _cancelButton;
    private BoxContainer? _testSection;

    private void BuildRaids()
    {
        var columns = TarkovTheme.Row(14); columns.VerticalExpand = true;
        var briefing = TarkovTheme.Column(12);
        briefing.AddChild(_conditionsTitle);
        briefing.AddChild(_conditions);
        briefing.AddChild(_mapBriefing);
        briefing.AddChild(_eventBriefing);
        briefing.AddChild(_activeRaid);
        briefing.AddChild(TarkovTheme.Label(Loc.GetString("ts-raid-briefing"), true));
        briefing.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-raid-briefing-text")));
        var departure = TarkovTheme.Column(8);
        departure.AddChild(_departure); departure.AddChild(_queueDetails);
        _queueNotice.FontColorOverride = TarkovTheme.Warning; departure.AddChild(_queueNotice);
        _readyButton = TarkovTheme.Button(Loc.GetString("ts-ready-departure"), () => Request(TarkovAction.Ready), true);
        _readyButton.MinHeight = 46;
        _cancelButton = TarkovTheme.Button(Loc.GetString("tarkov-cancel-ready"), () => Request(TarkovAction.CancelReady));
        departure.AddChild(_readyButton); departure.AddChild(_cancelButton);
        briefing.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-extraction-briefing")));
        _testSection = TarkovTheme.Column(8);
        _testSection.AddChild(TarkovTheme.Label(Loc.GetString("ts-solo-test")));
        _testSection.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-solo-test-help")));
        _testSection.AddChild(TarkovTheme.Button(Loc.GetString("ts-prepare-helpers"), () => Request(TarkovAction.TestPartner)));
        briefing.AddChild(_testSection);
        var team = TarkovTheme.Column();
        team.AddChild(TarkovTheme.Label(Loc.GetString("ts-your-party"), true));
        team.AddChild(List("party-roster"));
        team.AddChild(TarkovTheme.Button(Loc.GetString("tarkov-leave-party"), () => Request(TarkovAction.LeaveParty)));
        team.AddChild(List("party-invites"));
        team.AddChild(TarkovTheme.Label(Loc.GetString("ts-hub-players"), true));
        team.AddChild(List("party-players"));
        // Keep departure/cancellation reachable on small screens while the briefing can scroll independently.
        var left = TarkovTheme.Column(12);
        left.VerticalExpand = true;
        left.SizeFlagsStretchRatio = 1.1f;
        left.AddChild(TarkovTheme.Scroll(briefing));
        left.AddChild(TarkovTheme.Panel(departure, true, 16));
        columns.AddChild(left); columns.AddChild(TarkovTheme.Scroll(team)); _body.AddChild(columns);
    }

    private void UpdateRaids(TarkovStateEvent state)
    {
        _departure.Text = Loc.GetString("ts-queue-" + state.QueuePhase, ("seconds", state.QueueSeconds));
        var phase = TarkovRaidConditions.Key(state.DayPhase);
        _conditionsTitle.Text = Loc.GetString("ts-conditions-title", ("phase", Loc.GetString("ts-phase-" + phase)));
        _conditions.SetMessage(Loc.GetString("ts-conditions-" + phase));
        _mapBriefing.SetMessage(Loc.GetString("ts-map-briefing",
            ("size", Loc.GetString("ts-map-size-" + TarkovRaidPlan.SizeKey(state.RaidRadius))),
            ("biome", Loc.GetString("ts-biome-" + state.RaidBiome)), ("width", state.RaidRadius * 2)));
        var eventKey = TarkovRaidPlan.EventKey(state.RaidEvent);
        var eventState = Loc.GetString("ts-event-" + eventKey + "-stage-" + state.EventStage);
        _eventBriefing.SetMessage(Loc.GetString("ts-event-briefing", ("event", Loc.GetString("ts-event-" + eventKey)),
            ("detail", Loc.GetString("ts-event-" + eventKey + "-description")), ("status", eventState))
            + (state.ActiveRaid && state.EventStage < 2 ? "\n" + Loc.GetString("ts-event-countdown", ("time", TimeSpan.FromSeconds(state.EventSeconds).ToString(@"mm\:ss"))) : "")
            + (state.ActiveRaid && state.EventSector != "" && state.EventStage > 0 ? "\n" + Loc.GetString("ts-event-sector", ("sector", Loc.GetString(state.EventSector))) : ""));
        _activeRaid.SetMessage(Loc.GetString(state.ActiveRaid
            ? state.ActiveRaidSeconds > 0 ? "ts-raid-existing" : "ts-raid-preparing" : "ts-raid-next",
            ("time", TimeSpan.FromSeconds(state.ActiveRaidSeconds).ToString(@"mm\:ss"))));
        _queueDetails.Text = Loc.GetString("ts-party-readiness", ("ready", state.ReadyCount), ("total", state.PartyCount));
        _queueNotice.Text = state.RaidReentryBlocked ? Loc.GetString("tarkov-error-raid-death-lock") : state.QueueNotice == "" ? "" : Loc.GetString(state.QueueNotice);
        _queueNotice.Visible = state.RaidReentryBlocked || state.QueueNotice != "";
        if (_readyButton != null)
        {
            _readyButton.Disabled = state.RaidReentryBlocked || state.Ready || state.Location != "hub";
            _readyButton.Text = Loc.GetString(state.ActiveRaid ? "ts-ready-join" : "ts-ready-departure");
        }
        if (_cancelButton != null) _cancelButton.Disabled = !state.Ready;
        if (_testSection != null) _testSection.Visible = state.TestMode;
        RenderList("party-roster", state.Party, row =>
        {
            var item = TarkovTheme.Row(); item.AddChild(TarkovTheme.Label(row.Name));
            item.AddChild(TarkovTheme.Label(Loc.GetString(row.Flag ? "tarkov-ready" : "tarkov-not-ready"), muted: !row.Flag));
            return TarkovTheme.Panel(item, row.Flag);
        });
        RenderList("party-invites", state.Invites, row =>
        {
            var item = TarkovTheme.Column(6); item.AddChild(TarkovTheme.Label(Loc.GetString("ts-invitation", ("name", row.Name))));
            item.AddChild(TarkovTheme.Button(Loc.GetString("tarkov-accept-invite"), () => Request(TarkovAction.AcceptInvite, row.Id)));
            return TarkovTheme.Panel(item, true);
        });
        RenderList("party-players", state.Players.Where(p => p.Id != state.User), row =>
        {
            var item = TarkovTheme.Column(5); item.AddChild(TarkovTheme.Label(row.Name));
            item.AddChild(TarkovTheme.Label(row.Detail, muted: true));
            item.AddChild(TarkovTheme.Button(Loc.GetString("tarkov-invite"), () => Request(TarkovAction.Invite, row.Id), disabled: !row.Flag || state.Ready));
            return TarkovTheme.Panel(item);
        }, state.Ready.ToString());
    }
}
