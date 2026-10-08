// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._TarkovStation.Components;

/// <summary>The safe hub map. The component is present on the map root.</summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class TarkovHubComponent : Component;
