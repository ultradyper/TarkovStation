// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
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
    public static TarkovRaidPlan Create(string cycle, long sequence)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{cycle}:{sequence}"));
        var seed = (digest[0] | digest[1] << 8 | digest[2] << 16 | digest[3] << 24) & int.MaxValue;
        return new TarkovRaidPlan(seed, new[] { 40, 60, 80 }[digest[4] % 3],
            new[] { "Grasslands", "LowDesert", "Snow" }[digest[5] % 3], (TarkovRaidEventKind)(digest[6] % 2));
    }

    public static string SizeKey(int radius) => radius >= 80 ? "large" : radius >= 60 ? "medium" : "small";
    public static string EventKey(TarkovRaidEventKind kind) => kind == TarkovRaidEventKind.Migration ? "migration" : "airdrop";
}
