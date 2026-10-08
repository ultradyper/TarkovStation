// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using Content.Shared._TarkovStation;

namespace Content.Server._TarkovStation;

/// <summary>Server-only destination selection; cryptographic APIs must never enter client sandbox assemblies.</summary>
public static class TarkovRaidPlanner
{
    public static TarkovRaidPlan Create(string cycle, long sequence)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{cycle}:{sequence}"));
        var seed = (digest[0] | digest[1] << 8 | digest[2] << 16 | digest[3] << 24) & int.MaxValue;
        return new TarkovRaidPlan(seed, new[] { 40, 60, 80 }[digest[4] % 3],
            new[] { "Grasslands", "LowDesert", "Snow" }[digest[5] % 3], (TarkovRaidEventKind)(digest[6] % 2));
    }

}
