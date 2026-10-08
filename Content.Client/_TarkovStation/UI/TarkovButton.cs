// SPDX-License-Identifier: AGPL-3.0-or-later
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._TarkovStation.UI;

/// <summary>Readable native buttons with explicit hover/pressed/disabled feedback.</summary>
public sealed class TarkovButton : Button
{
    private readonly bool _primary;
    public TarkovButton(bool primary = false)
    {
        _primary = primary;
        Modulate = Color.White;
        ModulateSelfOverride = Color.White;
        MinHeight = 38;
        HorizontalExpand = true;
        DrawModeChanged();
    }
    protected override void DrawModeChanged()
    {
        base.DrawModeChanged();
        var color = DrawMode switch
        {
            DrawModeEnum.Disabled => "#3C4345",
            DrawModeEnum.Pressed => _primary ? "#BAC78B" : "#828D78",
            DrawModeEnum.Hover => _primary ? "#A5B276" : "#697565",
            _ => _primary ? "#87995F" : "#505E58",
        };
        StyleBoxOverride = new StyleBoxFlat
        {
            BackgroundColor = Color.FromHex(color), BorderColor = Color.FromHex(_primary ? "#C8D49C" : "#84918A"),
            BorderThickness = new Thickness(1), ContentMarginLeftOverride = 12, ContentMarginRightOverride = 12,
            ContentMarginTopOverride = 7, ContentMarginBottomOverride = 7,
        };
        if (Label != null) Label.FontColorOverride = DrawMode == DrawModeEnum.Disabled ? TarkovTheme.Muted : TarkovTheme.Text;
    }
}
