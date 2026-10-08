// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Client.Lobby.UI;

// A partial extension of the existing editor intentionally retains its original namespace.
public sealed partial class HumanoidProfileEditor
{
    public void ConfigureForTarkov()
    {
        TabContainer.SetTabVisible(1, false);
        TabContainer.SetTabVisible(2, false);
        TabContainer.SetTabVisible(3, false);
        MarkingsTab.Visible = false;
        TabContainer.SetTabVisible(4, false);
        ProfileHighlight.Visible = false;
        WarningLabel.Visible = false;
        SpeciesButton.Disabled = true;
        HeightSlider.Parent!.Visible = false;
        WidthSlider.Parent!.Visible = false;
        RandomizeEverythingButton.Visible = false;
        SpawnPriorityButton.Parent!.Visible = false;
        VoiceBarkButton.Parent!.Visible = false;
        BarkPitchSlider.Parent!.Parent!.Visible = false;
        SpriteView.Scale = new System.Numerics.Vector2(5);
    }
}
