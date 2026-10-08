// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Content.IntegrationTests;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Decals;
using Content.Server.Mind;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Components;
using Content.Shared.Access.Components;
using Content.Shared.Atmos;
using Content.Shared.Damage.Components;
using Content.Shared.Decals;
using Content.Shared.Gravity;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Light.Components;
using Content.Shared._NF.Shuttles;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.UnitTesting;
using Robust.UnitTesting.Pool;
using YamlDotNet.RepresentationModel;

namespace TarkovStation.HubBuilder;

/// <summary>
/// Creates an uninitialized authoring map using native entity, tile and decal APIs.
/// Validation initializes a separately reloaded copy; runtime state is never saved over the authoring map.
/// Run from the repository root. The destination is explicit to avoid overwriting hand-edited work.
/// </summary>
internal static class Program
{
    private static readonly List<object> Checks = new();

    public static async Task Main(string[] args)
    {
        var verify = args.Length == 3 && args[0] == "--verify";
        if (verify)
            args = args.Skip(1).ToArray();
        if (args.Length != 2 || (!verify && File.Exists(args[0])) || (verify && !File.Exists(args[0])))
            throw new ArgumentException("Usage: HubBuilder [--verify] <map.yml> <validation.json>; generation requires a new destination.");

        var output = Path.GetFullPath(args[0]);
        var report = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        PoolManager.Startup();
        try
        {
            var context = new ExternalTestContext("TarkovStation hub authoring", Console.Out);
            await using var pair = await PoolManager.GetServerClient(new PoolSettings
            {
                DummyTicker = false,
                Connected = true,
                Destructive = true,
                Fresh = true,
                Map = PoolManager.TestMap,
            }, context);

            await pair.Server.WaitPost(() =>
            {
                if (!verify)
                    Build(pair.Server.EntMan, output);
                ValidateWallPrototype(pair.Server, "server");
            });
            await pair.Client.WaitPost(() => ValidateWallPrototype(pair.Client, "client"));

            EntityUid validationMap = default;
            Entity<MapGridComponent> validationGrid = default;
            EntityUid walker = default;
            await pair.Server.WaitPost(() =>
            {
                var em = pair.Server.EntMan;
                using var reader = File.OpenText(output);
                Require(em.System<MapLoaderSystem>().TryLoadGeneric(reader, output, out var loaded), "Native reload");
                validationMap = loaded!.Maps.Single();
                var grid = loaded.Grids.Single();
                validationGrid = (grid, em.GetComponent<MapGridComponent>(grid));
                em.System<SharedMapSystem>().InitializeMap(validationMap);
                using (var preview = File.CreateText(Path.Combine(Path.GetDirectoryName(report)!, "runtime-preview.yml")))
                    Require(em.System<MapLoaderSystem>().TrySaveMap(validationMap, preview), "Save disposable initialized preview");
                walker = em.SpawnEntity("MobHuman", new EntityCoordinates(grid, new Vector2(0.5f, 0.5f)));
                var session = pair.Server.PlayerMan.Sessions.Single();
                var mind = em.System<MindSystem>().GetOrCreateMind(session.UserId);
                em.System<MindSystem>().TransferTo(mind, walker);
            });

            await pair.RunTicksSync(90);
            await pair.Server.WaitPost(() => CheckRuntime(pair.Server.EntMan, validationMap, validationGrid, walker));

            // Exercise real movement/collision and proximity-opened ordinary doors, without ghost/admin movement.
            var routes = new (string Name, Vector2 Start, Direction Direction, int Ticks, float MinDistance)[]
            {
                ("central hall east-west", new Vector2(-4.5f, 0.5f), Direction.East, 110, 6f),
                ("west wing entrance", new Vector2(-10.5f, -2.5f), Direction.South, 150, 4.5f),
                ("east wing entrance", new Vector2(9.5f, -2.5f), Direction.South, 150, 4.5f),
                ("west sealed docks circulation", new Vector2(-12.5f, -11.5f), Direction.South, 70, 3f),
                ("east sealed docks circulation", new Vector2(7.5f, -11.5f), Direction.South, 70, 3f),
            };
            foreach (var route in routes)
            {
                await pair.Server.WaitPost(() =>
                {
                    var em = pair.Server.EntMan;
                    em.System<SharedTransformSystem>().SetCoordinates(walker,
                        new EntityCoordinates(validationGrid, route.Start));
                    var mover = em.GetComponent<InputMoverComponent>(walker);
                    em.System<SharedMoverController>().SetVelocityDirection((walker, mover), route.Direction, 0, true);
                });
                await pair.RunTicksSync(route.Ticks);
                await pair.Server.WaitPost(() =>
                {
                    var em = pair.Server.EntMan;
                    var end = em.GetComponent<TransformComponent>(walker).LocalPosition;
                    em.System<SharedMoverController>().SetVelocityDirection(
                        (walker, em.GetComponent<InputMoverComponent>(walker)), route.Direction, 0, false);
                    var distance = Vector2.Distance(route.Start, end);
                    Checks.Add(new { route.Name, Start = route.Start.ToString(), End = end.ToString(), Distance = distance });
                    Require(distance >= route.MinDistance, route.Name);
                });
                await pair.RunTicksSync(10);
            }

            File.WriteAllText(report, JsonSerializer.Serialize(Checks, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            await pair.CleanReturnAsync();
        }
        finally
        {
            PoolManager.Shutdown();
        }
    }

    private static void Build(IEntityManager em, string output)
    {
        var maps = em.System<SharedMapSystem>();
        var loader = em.System<MapLoaderSystem>();
        var transform = em.System<SharedTransformSystem>();
        var metadata = em.System<MetaDataSystem>();
        var decals = em.System<DecalSystem>();
        var light = em.System<SharedPointLightSystem>();
        // Give the original orphan grid a map component before native initialization. Keeping
        // the same UID retains every child/link; the engine handles conversion and format-7 saving.
        var document = new YamlStream();
        using (var source = File.OpenText("Resources/Maps/Misc/terminal.yml"))
            document.Load(source);
        var yaml = (YamlMappingNode)document.Documents[0].RootNode;
        var groups = (YamlSequenceNode)yaml.Children[new YamlScalarNode("entities")];
        var rootGroup = (YamlMappingNode)groups.Children[0];
        var rootEntity = (YamlMappingNode)((YamlSequenceNode)rootGroup.Children[new YamlScalarNode("entities")]).Children[0];
        ((YamlSequenceNode)rootEntity.Children[new YamlScalarNode("components")]).Add(new YamlMappingNode
        {
            { "type", "Map" },
        });
        // Station marker is authoring metadata; let the native loader populate the restricted component.
        foreach (var component in ((YamlSequenceNode)rootEntity.Children[new YamlScalarNode("components")]).Children.OfType<YamlMappingNode>())
            if (component.Children.TryGetValue(new YamlScalarNode("type"), out var kind) && kind.ToString() == "BecomesStation")
                component.Children[new YamlScalarNode("id")] = new YamlScalarNode("TarkovHub");
        using var prepared = new StringWriter();
        document.Save(prepared, assignAnchors: false);
        using var sourceReader = new StringReader(prepared.ToString());
        Require(loader.TryLoadGeneric(sourceReader, "Resources/Maps/Misc/terminal.yml", out var loaded,
            new MapLoadOptions { DeserializationOptions = new DeserializationOptions { StoreYamlUids = true } }),
            "Load source as native map-grid root");
        var grid = loaded!.Grids.Single();
        EntityUid map = loaded.Maps.Single();
        var mapId = em.GetComponent<MapComponent>(map).MapId;
        Require(map == grid.Owner, "One native map-grid root");
        // A fixed hub must not create temporary split grids while its old docking apron is removed.
        grid.Comp.CanSplit = false;
        em.RemoveComponent<ShuttleComponent>(grid);
        em.RemoveComponent<FTLDriveComponent>(grid);
        em.RemoveComponent<ImplicitRoofComponent>(grid);
        em.System<SharedPhysicsSystem>().SetBodyType(grid, BodyType.Static);
        metadata.SetEntityName(map, "Перевал / 17 — заброшенный терминал");
        var gravity = em.EnsureComponent<GravityComponent>(grid);
        gravity.Enabled = true;
        gravity.Inherent = true;
        var gas = new GasMixture(Atmospherics.CellVolume) { Temperature = Atmospherics.T20C };
        gas.SetMoles(Gas.Oxygen, 21.824879f);
        gas.SetMoles(Gas.Nitrogen, 82.10312f);
        gas.MarkImmutable();
        em.System<AtmosphereSystem>().SetMapAtmosphere(map, false, gas);
        // Keep the bunker readable under native lighting; the static renderer is full-bright.
        maps.SetAmbientLight(mapId, Color.FromHex("#7C8087"));

        List<(EntityUid Uid, string Proto, Vector2 Pos, Angle Rotation)> Entities()
        {
            var result = new List<(EntityUid, string, Vector2, Angle)>();
            var query = em.AllEntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var meta, out var xform))
            {
                if (xform.ParentUid == grid.Owner)
                    result.Add((uid, meta.EntityPrototype?.ID ?? "", xform.LocalPosition, xform.LocalRotation));
            }
            return result;
        }

        var walls = new HashSet<Vector2i>();
        var original = Entities();
        EntityUid Place(string proto, float x, float y, Angle? angle = null)
        {
            var uid = em.SpawnEntity(proto, new EntityCoordinates(grid, new Vector2(x, y)));
            if (angle != null)
                transform.SetLocalRotation(uid, angle.Value);
            return uid;
        }

        // Native deletion removes dependent contents; native spawning preserves valid UIDs and anchors.
        foreach (var ent in original)
        {
            var proto = ent.Proto;
            var tile = new Vector2i((int)MathF.Floor(ent.Pos.X), (int)MathF.Floor(ent.Pos.Y));
            if (proto.StartsWith("Wall") || proto.Contains("Window") || proto == "Grille" || proto.StartsWith("AirlockExternal"))
            {
                em.DeleteEntity(ent.Uid);
                if (walls.Add(tile))
                {
                    var wall = Place(proto.StartsWith("AirlockExternal") ? "WallSolidRust" : "TarkovStationHubWall",
                        tile.X + 0.5f, tile.Y + 0.5f);
                    em.EnsureComponent<GodmodeComponent>(wall);
                }
                continue;
            }

            if (proto.StartsWith("Poster") || proto.StartsWith("Sign") || proto.StartsWith("PottedPlant")
                || proto.StartsWith("VendingMachine") || proto.StartsWith("RandomVending") || proto.StartsWith("ArrivalsShuttleTimer")
                || proto.StartsWith("SpawnPoint") || proto.StartsWith("Firelock") || proto == "AtmosDeviceFanTiny"
                || proto is "BarSignEngineChange" or "SS13Memorial" or "RandomSpawner" or "ChessBoard"
                or "BlockGameArcade" or "SpaceVillainArcadeFilled" or "BoozeDispenser" or "SodaDispenser"
                or "PaperBin10" or "DrinkGlass" or "DrinkShaker")
            {
                em.DeleteEntity(ent.Uid);
                continue;
            }

            if (proto is "AirlockGlass" or "Windoor")
            {
                em.DeleteEntity(ent.Uid);
                Place("AirlockMaint", ent.Pos.X, ent.Pos.Y, ent.Rotation);
                continue;
            }

            if ((proto == "Chair" && ent.Pos.Y < -10) || proto == "ComfyChair")
            {
                em.DeleteEntity(ent.Uid);
                continue;
            }

            if (proto == "BookshelfFilled" || proto == "TableCarpet")
            {
                em.DeleteEntity(ent.Uid);
                Place(proto == "TableCarpet" ? "TableWood" : "Rack", ent.Pos.X, ent.Pos.Y, ent.Rotation);
            }
        }

        foreach (var ent in Entities())
        {
            if (em.HasComponent<AccessReaderComponent>(ent.Uid))
                em.RemoveComponent<AccessReaderComponent>(ent.Uid);
            if (ent.Proto is "AlwaysPoweredWallLight" or "SmallLight")
            {
                light.SetColor(ent.Uid, Color.FromHex(ent.Pos.Y < -10 ? "#CB8A50" : "#DEC39B"));
                light.SetEnergy(ent.Uid, 0.7f);
                light.SetRadius(ent.Uid, 5f);
            }
        }

        var tileDefs = IoCManager.Resolve<ITileDefinitionManager>();
        Tile Tile(string name) => new(tileDefs[name].TileId);
        var allTiles = maps.GetAllTiles(grid, grid.Comp).ToArray();
        var floor = allTiles.Where(t => tileDefs[t.Tile.TypeId].ID != "Lattice").Select(t => t.GridIndices).ToHashSet();
        var reachable = new HashSet<Vector2i>();
        var queue = new Queue<Vector2i>();
        queue.Enqueue(new Vector2i(0, 0));
        var directions = new[] { new Vector2i(1, 0), new Vector2i(-1, 0), new Vector2i(0, 1), new Vector2i(0, -1) };
        while (queue.TryDequeue(out var at))
        {
            if (!floor.Contains(at) || walls.Contains(at) || !reachable.Add(at))
                continue;
            foreach (var step in directions)
                queue.Enqueue(at + step);
        }

        foreach (var tile in allTiles)
        {
            var p = tile.GridIndices;
            if (!reachable.Contains(p) && !walls.Contains(p))
            {
                maps.SetTile(grid, grid.Comp, p, Tile("Space"));
                continue;
            }
            var name = p.Y >= 3 ? "FloorSteelDirty" : p.Y < -5 ? "FloorDark" : "FloorSteelDirty";
            if (p.Y is -10 or -17 || (p.Y == -2 && p.X >= -4 && p.X <= 2))
                name = "FloorSteelDamaged";
            maps.SetTile(grid, grid.Comp, p, Tile(name));
        }

        foreach (var ent in Entities())
        {
            var at = new Vector2i((int)MathF.Floor(ent.Pos.X), (int)MathF.Floor(ent.Pos.Y));
            if (!floor.Contains(at) || (!reachable.Contains(at) && !walls.Contains(at)))
                em.DeleteEntity(ent.Uid);
        }

        var decalGrid = em.GetComponent<DecalGridComponent>(grid);
        foreach (var id in decalGrid.DecalIndex.Keys.ToArray())
            decals.RemoveDecal(grid, id);

        // Wear follows wall edges, sealed docking thresholds and intentional damage patches.
        foreach (var p in reachable.OrderBy(p => p.Y).ThenBy(p => p.X))
        {
            var edge = directions.Any(d => walls.Contains(p + d));
            if (edge)
                decals.TryAddDecal("DirtMedium", new EntityCoordinates(grid, p), out _, Color.FromHex("#8E8065A0"),
                    Angle.FromDegrees(((p.X * 7 + p.Y * 13) & 3) * 90), cleanable: true);
        }
        foreach (var (x, y) in new[] { (-12, -10), (-8, -10), (7, -10), (11, -10), (-12, -17), (11, -17),
                     (-4, 1), (-3, 1), (2, -1), (3, -1), (-12, 0), (11, 2), (-3, 5), (1, 5), (-12, -19), (11, -19) })
        {
            decals.TryAddDecal("Damaged", new EntityCoordinates(grid, x, y), out _, Color.FromHex("#AD9878"));
            decals.TryAddDecal("Rust", new EntityCoordinates(grid, x, y), out _, Color.FromHex("#9A6A43B0"));
        }
        foreach (var x in new[] { -3, -2, -1, 0, 1, 2 })
            decals.TryAddDecal("WarnLineGreyscaleN", new EntityCoordinates(grid, x, -2), out _, Color.FromHex("#A47D45"));

        // Planning room in the former memorial garden; no working raid console is implied.
        Place("TableReinforced", -1.5f, 4.5f);
        Place("TableReinforced", -0.5f, 4.5f);
        Place("ComputerBroken", 1.5f, 5.5f);
        Place("ChairFolding", -1.5f, 5.5f, Angle.FromDegrees(180));
        Place("Rack", -3.5f, 5.5f);

        // Improvised storage/workshop and medical preparation; keep the principal aisles clear.
        foreach (var (x, y) in new[] { (-12.5f, -7.5f), (-12.5f, -9.5f), (-8.5f, -19.5f),
                     (7.5f, -19.5f), (11.5f, -19.5f) })
            Place("CrateGenericSteel", x, y);
        Place("Rack", -12.5f, -19.5f);
        Place("TableReinforced", -10.5f, -20.5f);
        Place("ComputerBroken", -8.5f, -7.5f);
        Place("StorageCanisterBroken", -8.5f, -9.5f);
        Place("MedicalBed", 11.5f, -7.5f, Angle.FromDegrees(90));
        Place("Bed", 11.5f, -9.5f, Angle.FromDegrees(90));
        Place("Rack", 7.5f, -7.5f);
        Place("CrateGenericSteel", 7.5f, -9.5f);
        Place("BarrelChemEmpty", -12.5f, 4.5f);
        Place("BarrelChemEmpty", 7.5f, 4.5f);
        Place("ComputerBroken", -9.5f, 4.5f);
        Place("ScrapAirlock1", -13.15f, -17.45f);
        Place("ScrapCamera", 12.8f, -10.2f);
        Place("ScrapTube", -7.2f, -18.1f);

        foreach (var (x, y) in new[] { (-13.5f, -13.5f), (-13.5f, -15.5f), (-7.5f, -13.5f),
                     (6.5f, -13.5f), (12.5f, -13.5f), (12.5f, -15.5f) })
            Place("ChairFolding", x, y);
        foreach (var (x, y) in new[] { (-2.5f, 0.5f), (-0.5f, 0.5f), (1.5f, 0.5f),
                     (-3.5f, -0.5f), (0.5f, -0.5f), (3.5f, -0.5f) })
            Place("SpawnPointLatejoin", x, y);

        // Verify the explicit brief before writing. Keep all state pre-mapinit.
        Require(!Entities().Any(e => e.Proto.Contains("Window") || e.Proto.StartsWith("AirlockExternal")
            || e.Proto.Contains("Nanotrasen") || e.Proto.StartsWith("Poster") || e.Proto.StartsWith("RandomVending")
            || e.Proto == "ArrivalsShuttleTimer"),
            "No windows, exterior airlocks, NT branding or shuttle timers");
        using var writer = new StringWriter();
        Require(loader.TrySaveMap(map, writer, new SerializationOptions { ExpectPreInit = true }), "Native authoring save");
        File.WriteAllText(output, "# SPDX-License-Identifier: AGPL-3.0-or-later\n"
            + "# Adapted from Resources/Maps/Misc/terminal.yml, Reserve-Station 2db0901b46.\n"
            + "# Native uninitialized authoring map; see Tools/_TarkovStation/HubBuilder and hub/README.md.\n\n"
            + writer.ToString().Replace("\r\n", "\n"));
        Checks.Add(new { Authoring = output, SourceEntities = original.Count, ReachableFloorTiles = reachable.Count, HullWallTiles = walls.Count });
        maps.DeleteMap(mapId);
    }

    private static void CheckRuntime(IEntityManager em, EntityUid map, Entity<MapGridComponent> grid, EntityUid walker)
    {
        Require(em.GetComponent<GravityComponent>(grid).Enabled, "Gravity enabled");
        var atmos = em.System<AtmosphereSystem>();
        var probes = new[] { new Vector2(0.5f, 0.5f), new Vector2(-10.5f, -7.5f), new Vector2(9.5f, -7.5f),
            new Vector2(-11.5f, -19.5f), new Vector2(9.5f, -19.5f) };
        foreach (var probe in probes)
        {
            em.System<SharedTransformSystem>().SetCoordinates(walker, new EntityCoordinates(grid, probe));
            var mix = atmos.GetContainingMixture((walker, em.GetComponent<TransformComponent>(walker)));
            Require(mix != null && mix.Pressure > 80 && mix.Pressure < 130 && mix.GetMoles(Gas.Oxygen) > 15,
                $"Breathable atmosphere at {probe}");
        }
        Require(!em.HasComponent<ShuttleComponent>(grid) && em.GetComponent<BecomesStationComponent>(grid).Id == "TarkovHub",
            "Hub has its own station marker and no arrivals shuttle");
        var receivers = em.AllEntityQueryEnumerator<ApcPowerReceiverComponent, TransformComponent>();
        var powered = 0;
        var total = 0;
        while (receivers.MoveNext(out _, out var receiver, out var xform))
        {
            if (xform.ParentUid != grid.Owner)
                continue;
            total++;
            if (receiver.Powered)
                powered++;
        }
        Checks.Add(new { PowerReceivers = total, Powered = powered });
        Require(total > 0 && powered == total, "Existing power network supplies all receivers");
        Checks.Add(new { RuntimeMap = map.ToString(), AtmosphereProbes = probes.Length, Gravity = true });
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException("Hub check failed: " + name);
        Console.WriteLine("HUB CHECK PASS: " + name);
    }

    private static void ValidateWallPrototype(RobustIntegrationTest.IntegrationInstance instance, string side)
    {
        var prototypes = instance.ResolveDependency<IPrototypeManager>();
        Require(prototypes.TryGetMapping<EntityPrototype>("TarkovStationHubWall", out var mapping),
            $"Hub wall prototype available on {side}");
        // Validate the already composed mapping, including its upstream parent. Validating only
        // our directory cannot resolve WallRiveted and all of its inheritance chain.
        var composed = (MappingDataNode)mapping!.Copy();
        composed.Remove("type");
        var errors = instance.ResolveDependency<ISerializationManager>().ValidateNode<EntityPrototype>(composed).GetErrors().ToArray();
        foreach (var error in errors)
            Console.WriteLine($"HUB PROTOTYPE ERROR ({side}): {error.ErrorReason}");
        Checks.Add(new { Side = side, HubWallPrototypeSchemaErrors = errors.Length });
        Require(errors.Length == 0, $"Composed hub wall schema on {side}");
    }
}
