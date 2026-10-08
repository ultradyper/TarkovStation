// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using System.Numerics;
using Content.Server._TarkovStation.Persistence;
using Content.Shared._TarkovStation.Components;
using Content.Shared._TarkovStation;
using Content.Shared._TarkovStation.Prototypes;
using Robust.Shared.Map;

namespace Content.Server._TarkovStation;

public sealed partial class TarkovSystem
{
    private void PopulateRaidLoot(EntityUid map, TarkovRaidComponent raid)
    {
        var random = new Random(raid.Seed);
        var catalogue = _proto.EnumeratePrototypes<TarkovGoodsPrototype>().ToDictionary(g => g.ID);
        var count = Math.Clamp(6 + raid.Radius / 4 + (int)raid.DayPhase * 2, 8, 60);
        for (var i = 0; i < count; i++)
        {
            var zone = i % 3;
            var tableId = zone == 0 ? "TarkovStationOuterLoot" : zone == 1 ? "TarkovStationServiceLoot" : "TarkovStationFacilityLoot";
            Vector2 center;
            if (zone == 0)
            {
                var angle = i * 2.399963f;
                center = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * raid.Radius * 0.49f;
            }
            else if (zone == 1)
                center = new Vector2((i % 2 == 0 ? -1 : 1) * raid.Radius * 0.72f, -raid.Radius * 0.10f);
            else center = new Vector2(-7, -7);
            var offset = new Vector2(random.Next(-5, 6), random.Next(-5, 6));
            var position = center + offset + new Vector2(0.5f);
            ClearPad(map, position, 1);
            var crate = Spawn("CrateGenericSteel", new EntityCoordinates(map, position));
            TagTree(crate, false, raid.Id);
            _metadata.SetEntityName(crate, Loc.GetString("ts-loot-crate-" + zone));
            var table = _proto.Index<TarkovLootTablePrototype>(tableId);
            foreach (var goods in TarkovLootRoller.Roll(table, catalogue, random, TarkovRaidConditions.LootBudget(raid.DayPhase)))
            {
                var item = Spawn(goods.Product, new EntityCoordinates(map, position));
                TagTree(item, false, raid.Id); _entityStorage.Insert(item, crate);
            }
        }
        // The valuable central cache is guarded. Existing native HTN provides movement, targeting and firing.
        for (var i = 0; i < TarkovRaidConditions.Guards(raid.DayPhase, raid.Radius / 10); i++)
        {
            var position = (i % 3) switch
            {
                0 => new Vector2(-5 + i % 4 * 3, -5),
                1 => new Vector2(-raid.Radius * 0.62f + i % 3 * 2, -raid.Radius * 0.28f),
                _ => new Vector2(raid.Radius * 0.62f - i % 3 * 2, -raid.Radius * 0.28f),
            };
            ClearPad(map, position, 1);
            var archetype = (i % 3) switch
            {
                1 => "TarkovStationRaidGuardPatrol",
                2 => "TarkovStationRaidGuardVeteran",
                _ => "TarkovStationRaidGuard",
            };
            var guard = Spawn(archetype, new EntityCoordinates(map, position));
            foreach (var root in Roots(guard)) TagTree(root, false, raid.Id);
        }
    }
}
