// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._TarkovStation.Components;

/// <summary>Server-only accounting of damage for final deaths, including disconnected attackers.</summary>
[RegisterComponent]
public sealed partial class TarkovCombatCreditComponent : Component
{
    [DataField] public string Attacker = "";
    [DataField] public string Life = "";
    [DataField] public string Raid = "";
    [DataField] public TimeSpan LastDamage;
}
