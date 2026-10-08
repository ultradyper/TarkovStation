// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using Content.Shared._TarkovStation;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Localization;

namespace Content.Client._TarkovStation.UI;

public sealed partial class TarkovServiceWindow
{
    private string _nomination = "wealth";
    private readonly Dictionary<string, TarkovButton> _nominations = new();
    private readonly Label _standingsTitle = TarkovTheme.Label("", true);

    private void BuildLeaderboard()
    {
        var note = TarkovTheme.Column(6);
        note.AddChild(TarkovTheme.Label(Loc.GetString("ts-freedom-title"), true));
        note.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-freedom-story")));
        _body.AddChild(TarkovTheme.Panel(note, true));
        var tabs = TarkovTheme.Row(10);
        foreach (var nomination in new[] { "wealth", "kills", "previous" })
        {
            var button = TarkovTheme.Button(Loc.GetString("ts-nomination-" + nomination), () =>
            {
                _nomination = nomination; _fingerprints.Remove("standings");
                foreach (var (key, control) in _nominations) control.Disabled = key == nomination;
                if (_state != null) UpdateLeaderboard(_state);
            });
            _nominations[nomination] = button; tabs.AddChild(button);
        }
        _nominations["wealth"].Disabled = true; _body.AddChild(tabs); _body.AddChild(_standingsTitle);
        var headings = TarkovTheme.Row(12);
        var rankHeading = TarkovTheme.Label(Loc.GetString("ts-rank")); rankHeading.SetWidth = 50; rankHeading.HorizontalExpand = false;
        var capitalHeading = TarkovTheme.Label(Loc.GetString("ts-capital")); capitalHeading.SetWidth = 160; capitalHeading.HorizontalExpand = false;
        var resultsHeading = TarkovTheme.Label(Loc.GetString("ts-results")); resultsHeading.SetWidth = 220; resultsHeading.HorizontalExpand = false;
        headings.AddChild(rankHeading); headings.AddChild(TarkovTheme.Label(Loc.GetString("ts-contender")));
        headings.AddChild(capitalHeading); headings.AddChild(resultsHeading);
        _body.AddChild(TarkovTheme.Panel(headings)); _body.AddChild(TarkovTheme.Scroll(List("standings")));
    }

    private void UpdateLeaderboard(TarkovStateEvent state)
    {
        _standingsTitle.Text = Loc.GetString("ts-nomination-" + _nomination);
        var rows = _nomination == "previous" ? state.LastResults : _nomination == "kills"
            ? state.Leaderboard.OrderByDescending(r => r.Count).ThenByDescending(r => r.Price).ToList()
            : state.Leaderboard.OrderByDescending(r => r.Price).ThenByDescending(r => r.Count).ToList();
        var rank = 0;
        RenderList("standings", rows, row =>
        {
            var number = ++rank;
            var line = TarkovTheme.Row(14);
            var place = TarkovTheme.Label(number.ToString("00"), true); place.SetWidth = 50; place.HorizontalExpand = false; line.AddChild(place);
            var identity = TarkovTheme.Column(3); identity.SizeFlagsStretchRatio = 1;
            identity.AddChild(TarkovTheme.Label(row.Name));
            if (row.Name == state.CharacterName) identity.AddChild(TarkovTheme.Label(Loc.GetString("ts-you"), muted: true));
            line.AddChild(identity);
            var amount = TarkovTheme.Label(Loc.GetString("ts-price", ("price", row.Price))); amount.SetWidth = 160; amount.HorizontalExpand = false; line.AddChild(amount);
            var detail = TarkovTheme.Label(row.Detail, muted: true); detail.SetWidth = 220; detail.HorizontalExpand = false; line.AddChild(detail);
            return TarkovTheme.Panel(line, number <= 3 || row.Name == state.CharacterName, 14);
        }, _nomination + state.CharacterName);
    }
}
