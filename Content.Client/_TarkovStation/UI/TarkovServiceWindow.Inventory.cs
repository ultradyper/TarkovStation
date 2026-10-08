// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using System.Numerics;
using Content.Shared._TarkovStation;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Localization;

namespace Content.Client._TarkovStation.UI;

public sealed partial class TarkovServiceWindow
{
    private readonly LineEdit _search = new() { HorizontalExpand = true, MinHeight = 36 };
    private SpriteView? _portrait;
    private readonly OptionButton _marketCategory = new() { MinWidth = 190, MinHeight = 34 };
    private readonly List<string> _marketCategories = new();
    private string _category = "";
    private int _rarity = -1;
    private readonly OptionButton _marketRarity = new() { MinWidth = 150, MinHeight = 34 };
    private string _categoriesKey = "";
    private string _shopSection = "goods";
    private readonly Dictionary<string, TarkovButton> _shopSections = new();

    private void BuildShop()
    {
        var columns = TarkovTheme.Row(14); columns.VerticalExpand = true;
        var merchant = TarkovTheme.Column(10); merchant.SetWidth = 218; merchant.HorizontalExpand = false;
        _portrait = new SpriteView(_entities) { SetSize = new Vector2(180, 170), Scale = new Vector2(4), HorizontalAlignment = HAlignment.Center };
        merchant.AddChild(TarkovTheme.Panel(_portrait, true));
        merchant.AddChild(TarkovTheme.Label(Loc.GetString("ts-trader-name"), true));
        merchant.AddChild(TarkovTheme.Label(Loc.GetString("ts-trader-role"), muted: true));
        merchant.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-trader-story")));
        merchant.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-trader-delivery")));
        merchant.AddChild(TarkovTheme.Button(Loc.GetString("tarkov-emergency-kit"), () => Request(TarkovAction.EmergencyKit)));
        var merchantScroll = TarkovTheme.Scroll(merchant);
        var merchantPanel = TarkovTheme.Panel(merchantScroll);
        merchantPanel.SetWidth = 244; merchantPanel.HorizontalExpand = false;
        columns.AddChild(merchantPanel);
        var market = TarkovTheme.Column(10); market.VerticalExpand = true;
        var sections = TarkovTheme.Row(8);
        foreach (var section in new[] { "goods", "kits", "sell" })
        {
            var button = TarkovTheme.Button(Loc.GetString("ts-shop-" + section), () =>
            {
                _shopSection = section; _fingerprints.Remove("market");
                foreach (var (id, control) in _shopSections) control.Disabled = id == section;
                if (_state != null) UpdateShop(_state);
            });
            _shopSections[section] = button; sections.AddChild(button);
        }
        _shopSections["goods"].Disabled = true;
        market.AddChild(sections);
        _search.PlaceHolder = Loc.GetString("ts-search");
        _search.OnTextChanged += _ => { _fingerprints.Remove("market"); if (_state != null) UpdateShop(_state); };
        var filters = TarkovTheme.Row(8); filters.AddChild(_search); filters.AddChild(_marketCategory); filters.AddChild(_marketRarity);
        _marketRarity.AddItem(Loc.GetString("ts-rarity-all"), 0);
        for (var rarity = 0; rarity <= 6; rarity++) _marketRarity.AddItem(Loc.GetString("ts-rarity-" + rarity), rarity + 1);
        _marketRarity.SelectId(0);
        _marketRarity.OnItemSelected += e =>
        {
            _marketRarity.SelectId(e.Id); _rarity = e.Id - 1;
            _fingerprints.Remove("market"); if (_state != null) UpdateShop(_state);
        };
        _marketCategory.OnItemSelected += e =>
        {
            _marketCategory.SelectId(e.Id); _category = _marketCategories[e.Id];
            _fingerprints.Remove("market"); if (_state != null) UpdateShop(_state);
        };
        market.AddChild(filters);
        market.AddChild(TarkovTheme.Scroll(List("market")));
        columns.AddChild(market); _body.AddChild(columns);
    }

    private void UpdateShop(TarkovStateEvent state)
    {
        if (_portrait != null && state.ServiceSource is { } source) _portrait.SetEntity(source);
        IEnumerable<TarkovRow> items = _shopSection switch { "kits" => state.Kits, "sell" => state.Stash, _ => state.Shop };
        var categories = items.Select(r => r.Category).Where(c => c != "").Distinct().OrderBy(c => c).ToArray();
        var categoriesKey = _shopSection + string.Join('|', categories);
        if (categoriesKey != _categoriesKey)
        {
            _categoriesKey = categoriesKey; _marketCategory.Clear(); _marketCategories.Clear();
            _marketCategory.AddItem(Loc.GetString("ts-category-all"), 0); _marketCategories.Add("");
            foreach (var category in categories) { _marketCategory.AddItem(Loc.GetString(category), _marketCategories.Count); _marketCategories.Add(category); }
            _marketCategory.SelectId(0); _category = "";
        }
        items = items.Where(r => (_rarity < 0 || r.Rarity == _rarity) && (_category == "" || r.Category == _category) && r.Name.Contains(_search.Text, StringComparison.OrdinalIgnoreCase));
        RenderList("market", items, row =>
        {
            var content = TarkovTheme.Column(8);
            var heading = TarkovTheme.Row(12); heading.AddChild(TarkovTheme.Icon(row.Prototype, _entities, 66));
            var text = TarkovTheme.Column(4);
            var name = TarkovTheme.Label(row.Name); name.ToolTip = row.Name; text.AddChild(name);
            var rarity = TarkovTheme.Label(Loc.GetString("ts-rarity-" + row.Rarity));
            rarity.FontColorOverride = TarkovTheme.RarityColor(row.Rarity); text.AddChild(rarity);
            text.AddChild(TarkovTheme.Paragraph(row.Detail)); heading.AddChild(text); content.AddChild(heading);
            var selling = _shopSection == "sell";
            var action = selling ? TarkovAction.Sell : _shopSection == "kits" ? TarkovAction.BuyKit : TarkovAction.Buy;
            content.AddChild(TarkovTheme.Button(Loc.GetString(selling ? "ts-sell-price" : "ts-buy-price", ("price", row.Price)),
                () => Request(action, row.Id), true, selling ? row.Price <= 0 : state.Balance < row.Price));
            return TarkovTheme.Panel(content);
        }, state.Balance + _shopSection + _search.Text + _category + _rarity, _shopSection == "goods" ? 2 : 1);
    }

    private void BuildStash()
    {
        _search.PlaceHolder = Loc.GetString("ts-search");
        _search.OnTextChanged += _ => { if (_state != null) UpdateStash(_state); };
        _body.AddChild(_search);
        var columns = new TarkovColumns(14); columns.VerticalExpand = true;
        var carried = TarkovTheme.Column(); carried.SizeFlagsStretchRatio = 1;
        carried.AddChild(TarkovTheme.Label(Loc.GetString("ts-carried"), true));
        carried.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-carried-note")));
        carried.AddChild(TarkovTheme.Scroll(List("carried")));
        var stash = TarkovTheme.Column(); stash.SizeFlagsStretchRatio = 1;
        stash.AddChild(TarkovTheme.Label(Loc.GetString("ts-stash"), true));
        stash.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-stash-note")));
        stash.AddChild(TarkovTheme.Scroll(List("stored")));
        columns.AddChild(carried); columns.AddChild(stash); _body.AddChild(columns);
    }

    private void UpdateStash(TarkovStateEvent state)
    {
        RenderList("carried", state.Inventory.Where(r => r.Name.Contains(_search.Text, StringComparison.OrdinalIgnoreCase)), row => Item(row,
            (Loc.GetString("ts-store-item"), () => Request(TarkovAction.Deposit, row.Id), state.QueuePhase == "generating")));
        RenderList("stored", state.Stash.Where(r => r.Name.Contains(_search.Text, StringComparison.OrdinalIgnoreCase)), row => Item(row,
            (Loc.GetString("ts-take-item"), () => Request(TarkovAction.Withdraw, row.Id), state.QueuePhase == "generating")));
    }
}
