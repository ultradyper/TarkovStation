// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using Content.Shared._TarkovStation;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Localization;

namespace Content.Client._TarkovStation.UI;

public sealed partial class TarkovServiceWindow
{
    private readonly OptionButton _contractKind = new();
    private readonly OptionButton _contractTarget = new();
    private readonly List<string> _targets = new();
    private readonly LineEdit _contractReward = new() { Text = "500", HorizontalExpand = true, MinHeight = 34 };
    private readonly LineEdit _contractCount = new() { Text = "1", HorizontalExpand = true, MinHeight = 34 };
    private readonly LineEdit _contractText = new() { HorizontalExpand = true, MinHeight = 34 };
    private string _targetFingerprint = "";

    private void BuildContracts()
    {
        var columns = TarkovTheme.Row(14); columns.VerticalExpand = true;
        var board = TarkovTheme.Column(); board.AddChild(TarkovTheme.Label(Loc.GetString("ts-contract-board"), true));
        board.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-contract-board-help")));
        board.AddChild(TarkovTheme.Scroll(List("contracts-board")));
        var form = TarkovTheme.Column(10); form.SetWidth = 310; form.HorizontalExpand = false;
        form.AddChild(TarkovTheme.Label(Loc.GetString("ts-new-contract"), true));
        form.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-contract-escrow")));
        for (var i = 0; i < 3; i++) _contractKind.AddItem(Loc.GetString("tarkov-contract-kind-" + new[] { "item", "kill", "custom" }[i]), i);
        _contractKind.SelectId(0); _contractKind.MinHeight = 34;
        _contractKind.OnItemSelected += e => { _contractKind.SelectId(e.Id); _targetFingerprint = ""; if (_state != null) UpdateContractTargets(_state); };
        _contractTarget.OnItemSelected += e => _contractTarget.SelectId(e.Id); _contractTarget.MinHeight = 34;
        form.AddChild(_contractKind); form.AddChild(_contractTarget);
        form.AddChild(TarkovTheme.Label(Loc.GetString("ts-contract-item-count"), muted: true)); form.AddChild(_contractCount);
        form.AddChild(TarkovTheme.Label(Loc.GetString("tarkov-reward"), muted: true)); form.AddChild(_contractReward);
        _contractText.PlaceHolder = Loc.GetString("tarkov-contract-description"); form.AddChild(_contractText);
        form.AddChild(TarkovTheme.Button(Loc.GetString("ts-post-contract"), () =>
        {
            if (!int.TryParse(_contractReward.Text, out var reward) || reward <= 0
                || !int.TryParse(_contractCount.Text, out var count) || count < 1) { InvalidInput(); return; }
            var kind = new[] { "item", "kill", "custom" }[_contractKind.SelectedId];
            Request(TarkovAction.CreateContract, kind == "custom" ? "" : _targets.ElementAtOrDefault(_contractTarget.SelectedId) ?? "",
                _contractText.Text, kind + ":" + (kind == "item" ? count : 1), reward);
        }, true));
        form.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-custom-contract-note")));
        var formPanel = TarkovTheme.Panel(TarkovTheme.Scroll(form), true);
        formPanel.SetWidth = 340; formPanel.HorizontalExpand = false;
        columns.AddChild(formPanel); columns.AddChild(board); _body.AddChild(columns);
    }

    private void UpdateContractTargets(TarkovStateEvent state)
    {
        var rows = (_contractKind.SelectedId == 1 ? state.Players.Where(p => p.Id != state.User) : state.Catalogue).ToArray();
        var key = _contractKind.SelectedId + string.Join('|', rows.Select(r => r.Id + r.Name));
        if (key == _targetFingerprint) return;
        _targetFingerprint = key; _contractTarget.Clear(); _targets.Clear();
        foreach (var row in rows)
        {
            _contractTarget.AddItem(row.Name, _targets.Count);
            _targets.Add(_contractKind.SelectedId == 1 ? row.Id : row.Prototype);
        }
        if (_targets.Count > 0) _contractTarget.SelectId(0);
        _contractTarget.Disabled = _contractKind.SelectedId == 2;
        _contractCount.Editable = _contractKind.SelectedId == 0;
    }

    private void UpdateContracts(TarkovStateEvent state)
    {
        UpdateContractTargets(state);
        RenderList("contracts-board", state.Contracts, row =>
        {
            var item = TarkovTheme.Column(8);
            item.AddChild(TarkovTheme.Paragraph(row.Name)); item.AddChild(TarkovTheme.Paragraph(row.Detail));
            var buttons = TarkovTheme.Row(8);
            if (row.State == "open" && row.Owner != state.User)
                buttons.AddChild(TarkovTheme.Button(Loc.GetString("tarkov-accept-contract"), () => Request(TarkovAction.AcceptContract, row.Id), true));
            if (row.State == "open" && row.Owner == state.User)
                buttons.AddChild(TarkovTheme.Button(Loc.GetString("tarkov-cancel-contract"), () => Request(TarkovAction.CancelContract, row.Id)));
            if (row.Flag && row.Kind == "item")
                buttons.AddChild(TarkovTheme.Button(Loc.GetString("tarkov-deliver-contract"), () => Request(TarkovAction.DeliverContract, row.Id), true));
            if (row.State == "accepted" && row.Owner == state.User && row.Kind == "custom")
                buttons.AddChild(TarkovTheme.Button(Loc.GetString("tarkov-confirm-contract"), () => Request(TarkovAction.ConfirmContract, row.Id), true));
            item.AddChild(buttons); return TarkovTheme.Panel(item, row.Flag);
        });
    }
}
