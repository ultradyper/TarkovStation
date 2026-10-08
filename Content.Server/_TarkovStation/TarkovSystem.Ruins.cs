// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using System.Numerics;
using Content.Shared._TarkovStation.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Mobs.Components;
using Content.Server.Decals;
using Content.Server.Power.Components;
using Content.Shared.Decals;
using Robust.Server.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._TarkovStation;

public sealed partial class TarkovSystem
{
    [Dependency] private GridFixtureSystem _gridFixtures = default!;
    [Dependency] private DecalSystem _decals = default!;

    private void PatchRuin(EntityUid planet, EntityUid ruin)
    {
        var offset = Transform(ruin).LocalPosition.Floored();
        var grid = Comp<MapGridComponent>(ruin);
        var tiles = _maps.GetAllTiles(ruin, grid).Select(t => (t.GridIndices + offset, t.Tile)).ToList();
        var decals = _decals.GetDecalsIntersecting(ruin, grid.LocalAABB);
        // Re-anchoring native cables cuts them. Recreate them at final coordinates instead.
        var cables = AllEntityQuery<CableComponent, TransformComponent>();
        var replacements = new List<(string Prototype, EntityCoordinates Coordinates)>();
        var remove = new List<EntityUid>();
        while (cables.MoveNext(out var uid, out _, out var transform))
        {
            if (transform.ParentUid != ruin || MetaData(uid).EntityPrototype is not { } proto) continue;
            replacements.Add((proto.ID, new EntityCoordinates(planet, transform.LocalPosition + offset)));
            remove.Add(uid);
        }
        foreach (var uid in remove) Del(uid);
        _gridFixtures.Merge(planet, ruin, offset, Angle.Zero);
        _biome.ReserveTiles(planet, grid.LocalAABB.Translated(offset), tiles);
        foreach (var replacement in replacements)
            _transform.AnchorEntity(Spawn(replacement.Prototype, replacement.Coordinates));
        foreach (var (_, decal) in decals)
            _decals.TryAddDecal(decal.Id, new EntityCoordinates(planet, decal.Coordinates + offset),
                out _, decal.Color, decal.Angle, decal.ZIndex, decal.Cleanable);
    }
    [Dependency] private SharedRoofSystem _roof = default!;

    private void ClearRaidRoutes(EntityUid map, TarkovRaidComponent raid)
    {
        var cells = new HashSet<Vector2i>();
        void Add(Vector2 point)
        {
            var tile = point.Floored();
            for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++) cells.Add(tile + new Vector2i(x, y));
        }
        var ringRadius = raid.Radius * 0.68f;
        var samples = (int)MathF.Ceiling(2 * MathF.PI * ringRadius * 2);
        for (var i = 0; i < samples; i++)
        {
            var angle = 2 * MathF.PI * i / samples;
            Add(new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * ringRadius);
        }
        // East/west evacuation approaches and one north approach to the central facility.
        for (var step = 0f; step <= raid.Radius * 0.84f; step += 0.5f)
        {
            Add(new Vector2(step, 0)); Add(new Vector2(-step, 0));
            if (step <= ringRadius) Add(new Vector2(0, step));
        }
        var tileDefinition = IoCManager.Resolve<ITileDefinitionManager>()["FloorDirt"];
        var tiles = cells.Select(c => (c, new Tile(tileDefinition.TileId))).ToList();
        var grid = Comp<MapGridComponent>(map);
        _maps.SetTiles(map, grid, tiles);
        _biome.ReserveTiles(map, new Box2(-raid.Radius, -raid.Radius, raid.Radius, raid.Radius), tiles);
        foreach (var cell in cells) _roof.SetRoof((map, grid, null), cell, false);
        var entities = AllEntityQuery<TransformComponent>();
        var remove = new List<EntityUid>();
        while (entities.MoveNext(out var uid, out var transform))
        {
            if (uid == map || transform.ParentUid != map || !transform.Anchored
                || !cells.Contains(transform.LocalPosition.Floored()) || HasComp<TarkovPlayerComponent>(uid)
                || HasComp<TarkovExitComponent>(uid) || HasComp<TarkovItemComponent>(uid) || HasComp<MobStateComponent>(uid)) continue;
            remove.Add(uid);
        }
        foreach (var uid in remove) QueueDel(uid);
    }

}
