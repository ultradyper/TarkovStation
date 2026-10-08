// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._TarkovStation.Components;

/// <summary>Non-interactive cargo silhouette descending before the real loot crate is created.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class TarkovAirdropVisualComponent : Component
{
    [DataField, AutoNetworkedField] public TimeSpan LandsAt;
    public const float FallSeconds = 4f;
}
