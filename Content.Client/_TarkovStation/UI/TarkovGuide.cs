// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Localization;

namespace Content.Client._TarkovStation.UI;

/// <summary>Shared paged introduction for mandatory onboarding and the hub help button.</summary>
public sealed class TarkovGuide : BoxContainer
{
    public const int Pages = 7;
    private int _page;
    private readonly Action _completed;
    private readonly bool _onboarding;

    public TarkovGuide(Action completed, bool onboarding)
    {
        _completed = completed;
        _onboarding = onboarding;
        Orientation = LayoutOrientation.Vertical;
        SeparationOverride = 14;
        HorizontalExpand = VerticalExpand = true;
        Build();
    }

    private void Build()
    {
        RemoveAllChildren();
        AddChild(TarkovTheme.Label(Loc.GetString("ts-guide-progress", ("page", _page + 1), ("total", Pages)), muted: true));
        AddChild(TarkovTheme.Label(Loc.GetString($"ts-guide-{_page + 1}-title"), true));
        var text = TarkovTheme.Column(18);
        text.AddChild(TarkovTheme.Paragraph(Loc.GetString($"ts-guide-{_page + 1}-text")));
        text.AddChild(TarkovTheme.Panel(TarkovTheme.Paragraph(Loc.GetString($"ts-guide-{_page + 1}-tip")), true));
        AddChild(TarkovTheme.Scroll(text));
        var actions = TarkovTheme.Row(12);
        actions.AddChild(TarkovTheme.Button(Loc.GetString("ts-entry-back"), () => { _page--; Build(); }, disabled: _page == 0));
        actions.AddChild(TarkovTheme.Button(Loc.GetString(_page + 1 < Pages ? "ts-guide-next"
            : _onboarding ? "ts-guide-create" : "ts-guide-close"), () =>
        {
            if (_page + 1 < Pages) { _page++; Build(); }
            else _completed();
        }, true));
        AddChild(actions);
    }
}

public sealed class TarkovGuideWindow : DefaultWindow
{
    public TarkovGuideWindow()
    {
        Title = Loc.GetString("ts-guide-title");
        MinSize = new Vector2(650, 430);
        SetSize = new Vector2(800, 540);
        Contents.AddChild(TarkovTheme.Panel(new TarkovGuide(Close, false)));
    }
}
