// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using System.Numerics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._TarkovStation.UI;

/// <summary>Measure text cards within equal column widths so the first rich label cannot consume the row.</summary>
public sealed class TarkovColumns : BoxContainer
{
    public TarkovColumns(int gap = 10)
    {
        Orientation = LayoutOrientation.Horizontal;
        SeparationOverride = gap;
        HorizontalExpand = true;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var children = Children.Where(c => c.Visible).ToArray();
        if (children.Length == 0) return Vector2.Zero;
        if (!float.IsFinite(availableSize.X)) return base.MeasureOverride(availableSize);
        var gaps = (SeparationOverride ?? 10) * (children.Length - 1);
        var width = MathF.Max(0, (availableSize.X - gaps) / children.Length);
        var height = 0f;
        foreach (var child in children)
        {
            child.Measure(new Vector2(width, availableSize.Y));
            height = MathF.Max(height, child.DesiredSize.Y);
        }
        return new Vector2(availableSize.X, height);
    }
}
