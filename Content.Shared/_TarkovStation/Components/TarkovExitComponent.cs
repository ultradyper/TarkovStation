// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._TarkovStation.Components;

/// <summary>A raid extraction location. It works only for live participants in this raid.</summary>
[RegisterComponent]
public sealed partial class TarkovExitComponent : Component
{
    [DataField] public string Raid = "";
}
