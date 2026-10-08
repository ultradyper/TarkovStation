// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;
using Content.Client.Stylesheets;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._TarkovStation.UI;

/// <summary>Industrial transit-terminal palette shared by the hub services and onboarding.</summary>
public static class TarkovTheme
{
    public static readonly Color Text = Color.FromHex("#F4EFDF");
    public static readonly Color Muted = Color.FromHex("#B7BBAF");
    public static readonly Color Accent = Color.FromHex("#D7DF9D");
    public static readonly Color Warning = Color.FromHex("#F0B088");
    public static Color RarityColor(int rarity) => Color.FromHex(rarity switch
    {
        1 => "#BED59B", 2 => "#A8CBE8", 3 => "#CDB5EB", 4 => "#E7CE92", _ => "#BEC6BE",
    });
    public static BoxContainer Column(int gap = 10) => new() { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = gap, HorizontalExpand = true };
    public static BoxContainer Row(int gap = 10) => new() { Orientation = BoxContainer.LayoutOrientation.Horizontal, SeparationOverride = gap, HorizontalExpand = true };
    public static Label Label(string text, bool heading = false, bool muted = false)
    {
        var label = new Label { Text = text, FontColorOverride = muted ? Muted : Text, ClipText = true, HorizontalExpand = true };
        if (heading) label.AddStyleClass(StyleClass.LabelHeadingBigger);
        return label;
    }
    public static RichTextLabel Paragraph(string text)
    {
        var label = new RichTextLabel { HorizontalExpand = true, Margin = new Thickness(0, 3, 0, 3) };
        label.SetMessage(FormattedMessage.FromUnformatted(text), Text);
        return label;
    }
    public static PanelContainer Panel(Control content, bool raised = false, float padding = 12)
    {
        var panel = new PanelContainer
        {
            HorizontalExpand = true, ModulateSelfOverride = Color.White,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex(raised ? "#6D786B" : "#59646A"),
                BorderColor = Color.FromHex(raised ? "#A4AD8C" : "#899593"), BorderThickness = new Thickness(1),
            },
        };
        content.Margin = new Thickness(padding);
        panel.AddChild(content);
        return panel;
    }
    public static Control Icon(string prototype, IEntityManager entities, float size = 56)
    {
        var icon = new TextureRect { MinSize = new Vector2(size), SetSize = new Vector2(size),
            Stretch = TextureRect.StretchMode.KeepAspectCentered, VerticalAlignment = Control.VAlignment.Center };
        if (prototype != "")
            icon.Texture = entities.System<SpriteSystem>().GetPrototypeIcon(new EntProtoId(prototype)).Default;
        return icon;
    }
    public static ScrollContainer Scroll(Control child)
    {
        var scroll = new ScrollContainer { HorizontalExpand = true, VerticalExpand = true, HScrollEnabled = false };
        scroll.AddChild(child);
        return scroll;
    }
    public static TarkovButton Button(string text, Action clicked, bool primary = false, bool disabled = false)
    {
        var button = new TarkovButton(primary) { Text = text, Disabled = disabled };
        button.OnPressed += _ => clicked();
        return button;
    }
}
