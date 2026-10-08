// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._TarkovStation.Components;

/// <summary>Authoritative player identity for this cycle and life; secrets are not replicated.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class TarkovPlayerComponent : Component
{
    [DataField, AutoNetworkedField] public string Faction = "";
    [DataField, AutoNetworkedField] public string Branch = "";
    [DataField] public string User = "";
    [DataField] public string Life = "";
    [DataField] public string Raid = "";
    [DataField] public bool Closed;
    [DataField] public TimeSpan ExtractAt;
    [DataField] public int LastExtractionTick;
    [DataField] public EntityUid? Exit;
    /// <summary>Last queue rejection/cancellation, retained until the player explicitly readies again.</summary>
    [DataField] public string QueueNotice = "";
    /// <summary>Current physical service. Revalidated on every action and UI refresh.</summary>
    [DataField] public EntityUid? ActiveTerminal;
}
