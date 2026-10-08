// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._TarkovStation.Components;

/// <summary>A tracked item instance. Persisted identifiers survive entity UID reassignment.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class TarkovItemComponent : Component
{
    [DataField] public string Id = "";
    [DataField, AutoNetworkedField] public bool Emergency;
    [DataField, AutoNetworkedField] public string FoundRaid = "";
}
