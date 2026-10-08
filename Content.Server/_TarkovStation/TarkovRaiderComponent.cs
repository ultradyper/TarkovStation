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
    /// <summary>Unreachable or exhausted targets expire instead of pinning the inhabitant forever.</summary>
    [DataField] public Dictionary<EntityUid, TimeSpan> IgnoredLoot = new();
}
