// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;

namespace Content.Server._TarkovStation.Persistence;

/// <summary>Durable mode data. Runtime entity identifiers never appear in this document.</summary>
public sealed class TarkovData
{
    public int Version { get; set; } = 2;
    public string Cycle { get; set; } = Guid.NewGuid().ToString("N");
    public long EndsUtc { get; set; }
    /// <summary>Number of successfully created raids this cycle; survives process recovery.</summary>
    public long RaidSequence { get; set; }
    public Dictionary<string, TarkovAccount> Accounts { get; set; } = new();
    public Dictionary<string, TarkovStoredItem> Items { get; set; } = new();
    public Dictionary<string, TarkovContract> Contracts { get; set; } = new();
    public Dictionary<string, TarkovTrade> Trades { get; set; } = new();
    public List<TarkovResult> Results { get; set; } = new();

    /// <summary>Copy mutable metadata; immutable profiles/item snapshots are shared safely without JSON round trips.</summary>
    internal TarkovData Copy() => new()
    {
        Version = Version, Cycle = Cycle, EndsUtc = EndsUtc, RaidSequence = RaidSequence,
        Accounts = Accounts.ToDictionary(p => p.Key, p => p.Value.Copy()),
        Items = Items.ToDictionary(p => p.Key, p => p.Value.Copy()),
        Contracts = Contracts.ToDictionary(p => p.Key, p => p.Value.Copy()),
        Trades = Trades.ToDictionary(p => p.Key, p => p.Value.Copy()),
        Results = Results.Select(r => r.Copy()).ToList(),
    };
}
