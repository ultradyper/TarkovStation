// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._TarkovStation.Components;

/// <summary>Interaction entry point for an actual hub service.</summary>
[RegisterComponent]
public sealed partial class TarkovTerminalComponent : Component
{
    [DataField] public string Page = "overview";
}
