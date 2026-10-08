// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._TarkovStation.Components;

/// <summary>Owns a raid's runtime state. All timers use game time.</summary>
[RegisterComponent]
public sealed partial class TarkovRaidComponent : Component
{
    [DataField] public string Id = "";
    [DataField] public int Seed;
    [DataField] public TimeSpan EndsAt;
    [DataField] public List<string> Participants = new();
    [DataField] public int Radius;
    /// <summary>Fixed at creation; late entry does not alter lighting, loot or threat budget.</summary>
    [DataField] public TarkovDayPhase DayPhase;
    [DataField] public int WarningStage;
}
