// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._TarkovStation;

/// <summary>Finite equipment and looting state, owned by each native HTN inhabitant.</summary>
[RegisterComponent]
public sealed partial class TarkovRaiderComponent : Component
{
    [DataField] public float ReloadSeconds = 2.5f;
    [DataField] public TimeSpan ReloadAt;
    [DataField] public TimeSpan NextLoot;
    [DataField] public float SearchRange = 18f;
    [DataField] public int LootedItems;
    [DataField] public int Reloads;
    [DataField] public bool KnifeMode;
    /// <summary>Native gun spread in degrees while this NPC holds the weapon.</summary>
    [DataField] public float MinimumSpread = 6f;
    [DataField] public float MaximumSpread = 14f;
    [DataField] public float FireRateScale = 0.7f;
    [DataField] public float ReactionSeconds = 0.6f;
    /// <summary>Unreachable or exhausted targets expire instead of pinning the inhabitant forever.</summary>
    [DataField] public Dictionary<EntityUid, TimeSpan> IgnoredLoot = new();
}
