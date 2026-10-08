// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;

namespace Content.Shared._TarkovStation;

[Serializable, NetSerializable]
public enum TarkovRaidEventKind : byte
{
    Airdrop,
    Migration,
}

/// <summary>Stable pre-departure briefing. Population and later arrivals cannot reroll the destination.</summary>
public readonly record struct TarkovRaidPlan(int Seed, int Radius, string Biome, TarkovRaidEventKind Event)
{
    public static string SizeKey(int radius) => radius >= 80 ? "large" : radius >= 60 ? "medium" : "small";
    public static string EventKey(TarkovRaidEventKind kind) => kind == TarkovRaidEventKind.Migration ? "migration" : "airdrop";
}
