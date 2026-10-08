// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using Content.Shared._TarkovStation;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Localization;

namespace Content.Client._TarkovStation.UI;

public sealed partial class TarkovServiceWindow
{
    private readonly LineEdit _tradeMoney = new() { Text = "0", MinHeight = 36, HorizontalExpand = true };
    private readonly Label _tradeState = TarkovTheme.Label("", true);
    private TarkovButton? _confirmTrade;
    private TarkovButton? _cancelTrade;
    private TarkovButton? _setTradeMoney;

    private void BuildTrade()
    {
        var columns = TarkovTheme.Row(14); columns.VerticalExpand = true;
        var people = TarkovTheme.Column(); people.SetWidth = 265; people.HorizontalExpand = false;
        people.AddChild(TarkovTheme.Label(Loc.GetString("ts-trade-partner"), true));
        people.AddChild(TarkovTheme.Scroll(List("trade-players"))); columns.AddChild(people);
        var offer = TarkovTheme.Column(); offer.VerticalExpand = true;
        offer.AddChild(_tradeState); offer.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-trade-explanation")));
        var money = TarkovTheme.Row(); money.AddChild(_tradeMoney);
        _setTradeMoney = TarkovTheme.Button(Loc.GetString("tarkov-set-trade-money"), () =>
        {
            if (_state == null || !int.TryParse(_tradeMoney.Text, out var value) || value < 0) { InvalidInput(); return; }
            Request(TarkovAction.SetTradeMoney, _state.TradeId, amount: value);
        });
        money.AddChild(_setTradeMoney); offer.AddChild(TarkovTheme.Panel(money));
        var inventories = TarkovTheme.Row(12); inventories.VerticalExpand = true;
        var available = TarkovTheme.Column(); available.AddChild(TarkovTheme.Label(Loc.GetString("ts-trade-available")));
        available.AddChild(TarkovTheme.Scroll(List("trade-available")));
        var committed = TarkovTheme.Column(); committed.AddChild(TarkovTheme.Label(Loc.GetString("ts-trade-offer")));
        committed.AddChild(TarkovTheme.Scroll(List("trade-offer")));
        inventories.AddChild(available); inventories.AddChild(committed); offer.AddChild(inventories);
        var confirm = TarkovTheme.Row();
        _confirmTrade = TarkovTheme.Button(Loc.GetString("ts-trade-confirm"), () => Request(TarkovAction.AcceptTrade, _state?.TradeId ?? ""), true);
        _cancelTrade = TarkovTheme.Button(Loc.GetString("tarkov-cancel-trade"), () => Request(TarkovAction.CancelTrade, _state?.TradeId ?? ""));
        confirm.AddChild(_confirmTrade); confirm.AddChild(_cancelTrade); offer.AddChild(confirm);
        columns.AddChild(offer); _body.AddChild(columns);
    }

    private void UpdateTrade(TarkovStateEvent state)
    {
        var trading = state.TradeId != "";
        _tradeState.Text = !trading ? Loc.GetString("ts-trade-choose")
            : Loc.GetString("ts-trade-confirmations", ("you", Loc.GetString(state.TradeAccepted ? "ts-confirmed" : "ts-waiting")),
                ("partner", Loc.GetString(state.PartnerAccepted ? "ts-confirmed" : "ts-waiting")));
        if (_confirmTrade != null) _confirmTrade.Disabled = !trading || state.TradeAccepted || !state.Trade.Any(r => r.Id != "" || r.Price > 0);
        if (_cancelTrade != null) _cancelTrade.Disabled = !trading;
        if (_setTradeMoney != null) _setTradeMoney.Disabled = !trading;
        RenderList("trade-players", state.Players.Where(r => r.Id != state.User), row =>
        {
            var card = TarkovTheme.Column(6); card.AddChild(TarkovTheme.Label(row.Name));
            card.AddChild(TarkovTheme.Label(row.Detail, muted: true));
            card.AddChild(TarkovTheme.Button(Loc.GetString("tarkov-offer-trade"), () => Request(TarkovAction.OfferTrade, row.Id), disabled: trading || !row.Flag));
            return TarkovTheme.Panel(card);
        }, state.TradeId);
        RenderList("trade-available", state.Stash, row => Item(row,
            (Loc.GetString("ts-trade-add"), () => Request(TarkovAction.AddTradeItem, state.TradeId, extra: row.Id), !trading)), state.TradeId);
        RenderList("trade-offer", state.Trade, row =>
        {
            if (row.Id == "")
            {
                var cash = TarkovTheme.Column(4); cash.AddChild(TarkovTheme.Label(row.Name));
                cash.AddChild(TarkovTheme.Label(Loc.GetString("ts-trade-money", ("money", row.Price))));
                return TarkovTheme.Panel(cash, true);
            }
            return row.Owner == state.User ? Item(row,
                (Loc.GetString("tarkov-remove-trade"), () => Request(TarkovAction.RemoveTradeItem, state.TradeId, extra: row.Id), false)) : Item(row);
        });
    }
}
