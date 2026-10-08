// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using System.Numerics;
using Content.Client.Stylesheets;
using Content.Shared._TarkovStation;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Localization;

namespace Content.Client._TarkovStation.UI;

/// <summary>One service, one physical interaction. No navigation can grant access to another terminal.</summary>
public sealed partial class TarkovServiceWindow : DefaultWindow
{
    private readonly Action<TarkovRequestEvent> _send;
    private readonly IEntityManager _entities;
    private readonly BoxContainer _body = TarkovTheme.Column();
    private readonly Label _wallet = TarkovTheme.Label("");
    private readonly Label _cycle = TarkovTheme.Label("", muted: true);
    private readonly Label _feedback = TarkovTheme.Label("");
    private readonly Dictionary<string, BoxContainer> _lists = new();
    private readonly Dictionary<string, string> _fingerprints = new();
    private TarkovStateEvent? _state;
    public string Page { get; }

    public TarkovServiceWindow(string page, Action<TarkovRequestEvent> send, IEntityManager entities)
    {
        Page = page; _send = send; _entities = entities;
        Title = Loc.GetString("ts-title-" + page); TitleClass = StyleClass.LabelHeading;
        MinSize = new Vector2(900, 610); SetSize = new Vector2(1100, 710);
        FindControl<Label>("TitleLabel").FontColorOverride = TarkovTheme.Text;
        var root = TarkovTheme.Column(12);
        var heading = TarkovTheme.Row(12);
        var identity = TarkovTheme.Column(3);
        identity.AddChild(TarkovTheme.Label(Loc.GetString("ts-title-" + page), true));
        identity.AddChild(TarkovTheme.Label(Loc.GetString("ts-subtitle-" + page), muted: true));
        heading.AddChild(identity);
        var account = TarkovTheme.Column(3); account.HorizontalExpand = false; account.MinWidth = 230;
        account.AddChild(_wallet); account.AddChild(_cycle); heading.AddChild(account);
        root.AddChild(TarkovTheme.Panel(heading, true, 16));
        _feedback.MinHeight = 24; root.AddChild(_feedback);
        _body.VerticalExpand = true; root.AddChild(_body);
        root.AddChild(TarkovTheme.Label(Loc.GetString("ts-service-footnote"), muted: true));
        Contents.AddChild(TarkovTheme.Panel(root));
        switch (page)
        {
            case "shop": BuildShop(); break;
            case "stash": BuildStash(); break;
            case "raids": BuildRaids(); break;
            case "contracts": BuildContracts(); break;
            case "trade": BuildTrade(); break;
            case "leaderboard": BuildLeaderboard(); break;
        }
    }

    public void Update(TarkovStateEvent state)
    {
        _state = state;
        _wallet.Text = Loc.GetString("ts-wallet", ("money", state.Balance), ("reserved", state.Reserved));
        _cycle.Text = Loc.GetString("ts-cycle", ("time", TimeSpan.FromSeconds(state.RemainingSeconds).ToString(@"hh\:mm\:ss")));
        if (state.Message != "")
        {
            _feedback.Text = Loc.GetString(state.Message); _feedback.Visible = true;
            _feedback.FontColorOverride = state.Message.StartsWith("tarkov-error") ? TarkovTheme.Warning : TarkovTheme.Accent;
        }
        switch (Page)
        {
            case "shop": UpdateShop(state); break;
            case "stash": UpdateStash(state); break;
            case "raids": UpdateRaids(state); break;
            case "contracts": UpdateContracts(state); break;
            case "trade": UpdateTrade(state); break;
            case "leaderboard": UpdateLeaderboard(state); break;
        }
    }

    private void Request(TarkovAction action, string id = "", string text = "", string extra = "", int amount = 0)
        => _send(new TarkovRequestEvent { Action = action, Id = id, Text = text, Extra = extra, Amount = amount });

    private BoxContainer List(string id)
    {
        var list = TarkovTheme.Column(8); _lists[id] = list; return list;
    }

    private void RenderList(string id, IEnumerable<TarkovRow> input, Func<TarkovRow, Control> render, string context = "", int columns = 1)
    {
        var rows = input.ToArray();
        var key = context + string.Join('|', rows.Select(r => $"{r.Id};{r.Name};{r.Detail};{r.Price};{r.Owner};{r.Flag};{r.State};{r.Count}"));
        if (_fingerprints.TryGetValue(id, out var previous) && previous == key) return;
        _fingerprints[id] = key;
        _lists[id].RemoveAllChildren();
        if (rows.Length == 0) { _lists[id].AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-empty-" + id))); return; }
        foreach (var batch in rows.Chunk(columns))
        {
            if (columns == 1) _lists[id].AddChild(render(batch[0]));
            else
            {
                var line = new TarkovColumns(10);
                foreach (var row in batch) line.AddChild(render(row));
                if (batch.Length < columns) line.AddChild(new Control { HorizontalExpand = true });
                _lists[id].AddChild(line);
            }
        }
    }

    private Control Item(TarkovRow row, params (string Text, Action Click, bool Disabled)[] actions)
    {
        var body = TarkovTheme.Row(12);
        body.AddChild(TarkovTheme.Icon(row.Prototype, _entities));
        var info = TarkovTheme.Column(4);
        var title = TarkovTheme.Label(row.Name); title.ToolTip = row.Name; info.AddChild(title);
        var rarity = TarkovTheme.Label(Loc.GetString("ts-rarity-" + row.Rarity), muted: true);
        rarity.FontColorOverride = TarkovTheme.RarityColor(row.Rarity); info.AddChild(rarity);
        if (row.Detail != "") info.AddChild(TarkovTheme.Label(row.Detail, muted: true));
        if (row.Price > 0) info.AddChild(TarkovTheme.Label(Loc.GetString("ts-price", ("price", row.Price))));
        body.AddChild(info);
        if (actions.Length > 0)
        {
            var buttons = TarkovTheme.Column(5); buttons.SetWidth = 150; buttons.HorizontalExpand = false;
            foreach (var action in actions) buttons.AddChild(TarkovTheme.Button(action.Text, action.Click, disabled: action.Disabled));
            body.AddChild(buttons);
        }
        return TarkovTheme.Panel(body);
    }

    private void InvalidInput()
    {
        _feedback.Text = Loc.GetString("ts-invalid-number"); _feedback.FontColorOverride = TarkovTheme.Warning; _feedback.Visible = true;
    }
}
