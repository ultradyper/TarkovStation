// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using Content.Shared._TarkovStation.Prototypes;

namespace Content.Server._TarkovStation.Persistence;

/// <summary>Deterministic server rolls obey the zone's value ceiling and never select invalid/negative entries.</summary>
public static class TarkovLootRoller
{
    public static List<TarkovGoodsPrototype> Roll(TarkovLootTablePrototype table,
        IReadOnlyDictionary<string, TarkovGoodsPrototype> catalogue, Random random, float budgetMultiplier = 1f)
    {
        var result = new List<TarkovGoodsPrototype>();
        var remaining = (int)Math.Clamp(table.Budget * budgetMultiplier, 0, 100000);
        for (var i = 0; i < Math.Clamp(table.Rolls, 0, 12); i++)
        {
            var candidates = table.Entries.Where(e => e.Weight > 0 && catalogue.TryGetValue(e.Goods.Id, out var g)
                && g.Sell > 0 && g.Sell <= remaining).ToArray();
            var weight = candidates.Sum(e => (long)e.Weight);
            if (weight <= 0) break;
            var pick = random.NextInt64(weight);
            foreach (var candidate in candidates)
            {
                pick -= candidate.Weight;
                if (pick >= 0) continue;
                var goods = catalogue[candidate.Goods.Id];
                result.Add(goods); remaining -= goods.Sell; break;
            }
        }
        return result;
    }
}
