// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Server._TarkovStation.Persistence;
using Content.Server.Parallax;
using Content.Server.Procedural;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._TarkovStation;
using Content.Shared._TarkovStation.Components;
using Content.Shared.Access.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Ghost;
using Content.Server.Ghost.Roles.Components;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Procedural;
using Content.Shared.Salvage;
using Content.Shared.Light.Components;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Utility;

namespace Content.Server._TarkovStation;

public sealed partial class TarkovSystem
{
    [Dependency] private BiomeSystem _biome = default!;
    [Dependency] private DungeonSystem _dungeon = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private ShuttleSystem _shuttle = default!;
    [Dependency] private SharedEntityStorageSystem _entityStorage = default!;

    [Dependency] private TurfSystem _turf = default!;

    private Entity<TarkovRaidComponent>? CurrentRaid()
    {
        var query = AllEntityQuery<TarkovRaidComponent>();
        while (query.MoveNext(out var uid, out var raid))
            if (!EntityManager.IsQueuedForDeletion(uid)) return (uid, raid);
        return null;
    }

    private void InitializeRaids()
    {
        SubscribeLocalEvent<TarkovPlayerComponent, DamageChangedEvent>(OnPlayerDamage);
        SubscribeLocalEvent<TarkovPlayerComponent, MobStateChangedEvent>(OnPlayerState);
        SubscribeLocalEvent<TarkovPlayerComponent, ComponentShutdown>(OnRaidBodyShutdown);
        SubscribeLocalEvent<TarkovExitComponent, InteractHandEvent>(OnExitInteract);
        SubscribeLocalEvent<TarkovExitComponent, InteractUsingEvent>(OnExitUsing);
        SubscribeLocalEvent<TarkovExitComponent, ActivateInWorldEvent>(OnExitActivate);
    }

    private bool PendingDeployment(string user)
    {
        var query = AllEntityQuery<TarkovRaidComponent, TarkovGenerationComponent>();
        while (query.MoveNext(out _, out var raid, out var pending))
            if (!pending.Cancelled && raid.Participants.Contains(user)) return true;
        return false;
    }

    private void CancelReady(string user, string notice = "tarkov-queue-cancelled")
    {
        var changed = _ready.Remove(user);
        var generation = AllEntityQuery<TarkovRaidComponent, TarkovGenerationComponent>();
        while (generation.MoveNext(out _, out var raid, out var pending))
            if (!pending.Cancelled && raid.Participants.Contains(user)) { pending.Cancelled = true; changed = true; }
        var bots = AllEntityQuery<TarkovTestBotComponent, TarkovPlayerComponent>();
        while (bots.MoveNext(out _, out var bot, out var player))
            if (bot.OwnerUser == user) changed |= _ready.Remove(player.User);
        if (_ready.Count == 0) _queueEnds = null;
        if (!changed) return;
        if (FindPlayer(user) is { } body) Comp<TarkovPlayerComponent>(body).QueueNotice = notice;
        Log.Info($"Raid queue cancelled: user={user}, reason={notice}, remaining={_ready.Count}");
    }

    private string? Ready(string user)
    {
        if (_repository == null || _hub == null) return "tarkov-error-starting";
        var data = _repository.Read();
        var current = CurrentRaid();
        var duration = current == null || HasComp<TarkovGenerationComponent>(current.Value.Owner)
            ? _cfg.GetCVar(TarkovCVars.RaidSeconds)
            : Math.Max(0, (current.Value.Comp.EndsAt - _timing.CurTime).TotalSeconds);
        if (data.EndsUtc - Utc <= duration + _cfg.GetCVar(TarkovCVars.QueueSeconds))
            return "tarkov-error-closing";
        if (!data.Accounts.TryGetValue(user, out var account) || account.Location != "hub") return "tarkov-error-hub";
        if (data.Trades.Values.Any(t => t.Status == "open" && (t.A == user || t.B == user))) return "tarkov-error-trade";
        if (PendingDeployment(user)) return "tarkov-error-preparing";
        if (FindPlayer(user) is not { } body || !Alive(body)) return "tarkov-error-not-alive";
        if (!TrySession(user, out var session) || session.Status != Robust.Shared.Enums.SessionStatus.InGame)
            return "tarkov-queue-disconnected";
        Comp<TarkovPlayerComponent>(body).QueueNotice = "";
        _ready.Add(user);
        Log.Info($"Raid queue ready: user={user}, body={body}, party={account.Party}, status={session.Status}");
        if (_queueEnds == null) _queueEnds = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(3, _cfg.GetCVar(TarkovCVars.QueueSeconds)));
        var bots = AllEntityQuery<TarkovTestBotComponent, TarkovPlayerComponent>();
        while (bots.MoveNext(out _, out var bot, out var pc))
            if (bot.OwnerUser == user && !pc.Closed && pc.Raid == ""
                && (bot.Target || (account.Party != "" && data.Accounts[pc.User].Party == account.Party))) _ready.Add(pc.User);
        return null;
    }

    private void UpdateRaids()
    {
        if (_repository == null || _hub == null) return;
        var generation = AllEntityQuery<TarkovRaidComponent, TarkovGenerationComponent>();
        while (generation.MoveNext(out var uid, out var raid, out var pending))
        {
            if (pending.Cancelled || pending.Jobs.Any(j => j.IsFaulted || j.IsCanceled) || _timing.CurTime >= pending.Deadline)
            {
                // Native generation owns unfinished jobs. Keep the hidden map until they settle,
                // otherwise a cancelled job can keep creating entities on a deleted grid.
                if (pending.Jobs.Any(j => !j.IsCompleted)) continue;
                RejectDeployment(uid, raid, pending.Cancelled ? "tarkov-queue-cancelled" : "tarkov-error-generation");
                continue;
            }
            if (!pending.Jobs.All(j => j.IsCompletedSuccessfully)) continue;
            try
            {
                FinishGeneration(uid, raid, pending);
            }
            catch (Exception exception)
            {
                Log.Error($"Raid finalization failed: raid={raid.Id}: {exception}");
                RejectDeployment(uid, raid, "tarkov-error-generation");
            }
            RemCompDeferred<TarkovGenerationComponent>(uid);
        }
        var raids = AllEntityQuery<TarkovRaidComponent>();
        var data = _repository.Read();
        while (raids.MoveNext(out var uid, out var raid))
        {
            if (HasComp<TarkovGenerationComponent>(uid)) continue;
            var remaining = (raid.EndsAt - _timing.CurTime).TotalSeconds;
            var warning = remaining <= 60 ? 2 : remaining <= 300 ? 1 : 0;
            if (warning > raid.WarningStage)
            {
                raid.WarningStage = warning;
                foreach (var user in raid.Participants)
                    if (FindPlayer(user) is { } present && Comp<TarkovPlayerComponent>(present).Raid == raid.Id)
                        Feedback(user, TarkovFeedback.Warning);
            }
            if (_timing.CurTime >= raid.EndsAt)
            {
                foreach (var user in raid.Participants)
                    if (FindPlayer(user) is { } body && Comp<TarkovPlayerComponent>(body).Raid == raid.Id)
                        CloseLife(body);
                QueueDel(uid);
                continue;
            }
            if (!raid.Participants.Any(u => data.Accounts.TryGetValue(u, out var a) && a.Location == "raid" && a.Raid == raid.Id))
                QueueDel(uid);
        }
        var players = AllEntityQuery<TarkovPlayerComponent>();
        while (players.MoveNext(out var uid, out var player))
        {
            if (player.ExtractAt == TimeSpan.Zero || player.Closed) continue;
            if (!Alive(uid) || player.Exit == null || Deleted(player.Exit)
                || Transform(uid).MapUid != Transform(player.Exit.Value).MapUid
                || Vector2.Distance(_transform.GetWorldPosition(uid), _transform.GetWorldPosition(player.Exit.Value)) > 2.5f)
            {
                player.ExtractAt = TimeSpan.Zero;
                player.Exit = null;
                Feedback(player.User, TarkovFeedback.ExtractionCancelled);
                continue;
            }
            var countdown = (int)Math.Ceiling((player.ExtractAt - _timing.CurTime).TotalSeconds);
            if (countdown is > 0 and <= 3 && player.LastExtractionTick != countdown)
            {
                player.LastExtractionTick = countdown;
                Feedback(player.User, TarkovFeedback.ExtractionTick);
            }
            if (_timing.CurTime >= player.ExtractAt) CompleteExtraction(uid);
        }
        if (_queueEnds != null && _timing.CurTime >= _queueEnds)
            BeginRaid();
        UpdateTestBots();
    }

    private bool Alive(EntityUid uid) => TryComp<MobStateComponent>(uid, out var mob) && mob.CurrentState == MobState.Alive;

    private void RejectDeployment(EntityUid map, TarkovRaidComponent raid, string reason)
    {
        Log.Warning($"Raid departure rejected: raid={raid.Id}, reason={reason}, participants={string.Join(',', raid.Participants)}");
        foreach (var user in raid.Participants)
        {
            if (FindPlayer(user) is { } body) Comp<TarkovPlayerComponent>(body).QueueNotice = reason;
            if (TrySession(user, out var session)) SendState(session, reason);
        }
        QueueDel(map);
    }

    private void BeginRaid()
    {
        if (_repository == null) return;
        var current = CurrentRaid();
        if (current is { } preparing && HasComp<TarkovGenerationComponent>(preparing.Owner)) return;
        var data = _repository.Read();
        // NPC helpers never own a departure. Leaving/cancelling/disconnecting must not send them alone.
        var queuedBots = AllEntityQuery<TarkovTestBotComponent, TarkovPlayerComponent>();
        while (queuedBots.MoveNext(out _, out var bot, out var pc))
            if (!_ready.Contains(bot.OwnerUser)) _ready.Remove(pc.User);
        foreach (var user in _ready.ToArray())
        {
            if (!data.Accounts.TryGetValue(user, out var a) || a.TestBot) continue;
            if (FindPlayer(user) is not { } body || !Alive(body)) CancelReady(user, "tarkov-error-not-alive");
            else if (!TrySession(user, out var session) || session.Status != Robust.Shared.Enums.SessionStatus.InGame)
                CancelReady(user, "tarkov-queue-disconnected");
            else if (data.Trades.Values.Any(t => t.Status == "open" && (t.A == user || t.B == user)))
                CancelReady(user, "tarkov-error-trade");
        }
        var users = _ready.Where(u => data.Accounts.TryGetValue(u, out var a) && a.Location == "hub"
            && FindPlayer(u) is { } body && Alive(body)
            && (HasComp<TarkovTestBotComponent>(body) || (TrySession(u, out var s) && s.Status == Robust.Shared.Enums.SessionStatus.InGame)))
            .Where(u => !data.Trades.Values.Any(t => t.Status == "open" && (t.A == u || t.B == u)))
            .Where(u => data.Accounts[u].Party == "" || data.Accounts.Values.Where(a => a.Party == data.Accounts[u].Party)
                .All(a => a.Location != "hub" || _ready.Contains(a.User))).ToList();
        _queueEnds = _ready.Count > users.Count ? _timing.CurTime + TimeSpan.FromSeconds(_cfg.GetCVar(TarkovCVars.QueueSeconds)) : null;
        if (users.Count == 0)
        {
            _ready.RemoveWhere(u => !data.Accounts.TryGetValue(u, out var a) || a.Location != "hub" || FindPlayer(u) == null);
            if (_ready.Count == 0) _queueEnds = null;
            return;
        }
        if (!users.Any(u => !data.Accounts[u].TestBot))
        {
            foreach (var user in users) _ready.Remove(user);
            if (_ready.Count == 0) _queueEnds = null;
            Log.Warning("Rejected NPC-only raid departure");
            return;
        }
        Log.Info($"Raid roster locked: participants={string.Join(',', users)}");
        foreach (var u in users) _ready.Remove(u);
        if (current is { } existing)
        {
            JoinRaid(existing, users, data);
            return;
        }
        var radius = users.Count <= 4 ? 40 : users.Count <= 10 ? 60 : 80;
        EntityUid? map = null;
        try
        {
            map = _maps.CreateMap(out var mapId, runMapInit: false);
            var seed = Random.Shared.Next();
            var biome = new[] { "Grasslands", "LowDesert", "Snow" }[seed % 3];
            _biome.EnsurePlanet(map.Value, _proto.Index<BiomeTemplatePrototype>(biome), seed,
                mapLight: TarkovRaidConditions.Ambient(TarkovRaidConditions.Phase(data.RaidSequence)));
            var grid = Comp<MapGridComponent>(map.Value);
            grid.CanSplit = false;
            var border = EnsureComp<RestrictedRangeComponent>(map.Value);
            border.Range = radius;
            var raid = EnsureComp<TarkovRaidComponent>(map.Value);
            raid.Id = Guid.NewGuid().ToString("N");
            raid.Seed = seed;
            raid.Participants = users;
            raid.Radius = radius;
            raid.DayPhase = TarkovRaidConditions.Phase(data.RaidSequence);
            _metadata.SetEntityName(map.Value, Loc.GetString("tarkov-raid-name", ("seed", seed % 10000)));
            _biome.Preload(map.Value, Comp<BiomeComponent>(map.Value), new Box2(-radius, -radius, radius, radius));
            var pending = EnsureComp<TarkovGenerationComponent>(map.Value);
            pending.PartyGroups = users.ToDictionary(u => u, u => data.Accounts[u].Party == "" ? u : data.Accounts[u].Party);
            pending.Deadline = _timing.CurTime + TimeSpan.FromSeconds(120);
            pending.Jobs.Add(_dungeon.GenerateDungeonAsync(_proto.Index<DungeonConfigPrototype>(biome == "Snow" ? "TarkovStationSnowyLabs" : "TarkovStationExperiment"),
                map.Value, grid, Vector2i.Zero, seed));
            // Separate native ruin grids supply additional recognizable POIs around the dungeon.
            var paths = new[] { "/Maps/Lavaland/hermit_base.yml", "/Maps/Lavaland/front_desk.yml" };
            for (var i = 0; i < paths.Length; i++)
            {
                if (_loader.TryLoadGrid(mapId, new ResPath(paths[i]), out var ruin, new DeserializationOptions(),
                    Vector2.Zero))
                {
                    var center = new Vector2((i == 0 ? -1 : 1) * radius * 0.72f, -radius * 0.10f);
                    _transform.SetCoordinates(ruin.Value.Owner, new EntityCoordinates(map.Value, center - ruin.Value.Comp.LocalAABB.Center));
                    PatchRuin(map.Value, ruin.Value.Owner);
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Tarkov generation failed: {exception}");
            if (map != null) QueueDel(map.Value);
            foreach (var user in users)
                if (TrySession(user, out var session)) SendState(session, "tarkov-error-generation");
        }
    }

    private void FinishGeneration(EntityUid map, TarkovRaidComponent raid, TarkovGenerationComponent pending)
    {
        if (_repository == null) return;
        _maps.InitializeMap(map);
        // Keep the chosen expedition lighting stable. A random native offset would contradict the console.
        RemComp<LightCycleComponent>(map);
        _maps.SetAmbientLight(Comp<MapComponent>(map).MapId, TarkovRaidConditions.Ambient(raid.DayPhase));
        // Sun shadows have a separate native clock. Freeze them as well so a
        // daytime raid does not retain the random midnight/dusk shadow settings.
        RemComp<SunShadowCycleComponent>(map);
        var sunlight = EnsureComp<SunShadowComponent>(map);
        sunlight.Direction = raid.DayPhase == TarkovDayPhase.Evening ? new Vector2(-2.5f, -0.1f) : new Vector2(0.5f, -1f);
        sunlight.Alpha = raid.DayPhase switch
        {
            TarkovDayPhase.Day => 0.25f,
            TarkovDayPhase.Evening => 0.35f,
            _ => 0f,
        };
        Dirty(map, sunlight);
        var entities = AllEntityQuery<MetaDataComponent, TransformComponent>();
        while (entities.MoveNext(out var uid, out var meta, out var xform))
        {
            if (xform.MapUid != map || uid == map) continue;
            if (HasComp<AccessReaderComponent>(uid)) RemCompDeferred<AccessReaderComponent>(uid);
            if (HasComp<GhostRoleComponent>(uid)) RemCompDeferred<GhostRoleComponent>(uid);
            if (HasComp<GhostTakeoverAvailableComponent>(uid)) RemCompDeferred<GhostTakeoverAvailableComponent>(uid);
            if (Goods(meta.EntityPrototype?.ID ?? "") != null) TagTree(uid, false, raid.Id);
        }
        raid.EndsAt = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(30, _cfg.GetCVar(TarkovCVars.RaidSeconds)));
        foreach (var angle in new[] { 0f, MathF.PI })
        {
            var point = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (raid.Radius * 0.82f);
            ClearPad(map, point, 3);
            var exit = Spawn("TarkovStationExtractionBeacon", new EntityCoordinates(map, point + new Vector2(0.5f, 0.5f)));
            EnsureComp<TarkovExitComponent>(exit).Raid = raid.Id;
        }
        var roster = new List<(string User, string Life, List<TarkovStoredItem> Items)>();
        var positions = new Dictionary<string, EntityCoordinates>();
        var bodies = new Dictionary<string, EntityUid>();
        var groups = raid.Participants.GroupBy(u => pending.PartyGroups.GetValueOrDefault(u, u)).ToArray();
        var index = 0;
        foreach (var group in groups)
        {
            var angle = 2 * MathF.PI * index++ / Math.Max(1, groups.Length) + MathF.PI / 2;
            var center = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (raid.Radius * 0.68f);
            ClearPad(map, center, 4);
            var offset = 0;
            foreach (var user in group)
            {
                if (FindPlayer(user) is not { } body || !Alive(body)
                    || (!HasComp<TarkovTestBotComponent>(body)
                        && (!TrySession(user, out var connected) || connected.Status != Robust.Shared.Enums.SessionStatus.InGame)))
                {
                    RejectDeployment(map, raid, "tarkov-queue-disconnected");
                    return;
                }
                bodies[user] = body;
                roster.Add((user, Guid.NewGuid().ToString("N"), CaptureRoots(body, user, "raid")));
                var pos = center + new Vector2((offset % 2) * 2 - 1, (offset / 2) * 2 - 1) + new Vector2(0.5f);
                offset++;
                positions[user] = new EntityCoordinates(map, pos);
            }
        }

        ClearRaidRoutes(map, raid);
        PopulateRaidLoot(map, raid);
        for (var i = 0; i < TarkovRaidConditions.Monsters(raid.DayPhase, raid.Participants.Count); i++)
        {
            var pos = new Vector2((i % 3 - 1) * 8, (i / 3 - 1) * 8);
            ClearPad(map, pos, 1);
            Spawn("MobCarp", new EntityCoordinates(map, pos));
        }
        // Commit the whole roster once, after successful map construction and validation.
        var error = Write("system", "deploy-" + raid.Id, "deploy", d =>
        {
            var result = TarkovEconomy.Deploy(d, raid.Id, roster);
            if (result == null) d.RaidSequence++;
            return result;
        });
        if (error != null)
        {
            RejectDeployment(map, raid, error);
            return;
        }
        foreach (var member in roster)
        {
            var body = bodies[member.User];
            var player = Comp<TarkovPlayerComponent>(body);
            player.Raid = raid.Id;
            player.Life = member.Life;
            player.ExtractAt = TimeSpan.Zero;
            _transform.SetCoordinates(body, positions[member.User]);
            Log.Info($"Raid deployed: user={member.User}, raid={raid.Id}, body={body}");
            Feedback(member.User, TarkovFeedback.Departure);
            if (TrySession(member.User, out var session)) SendState(session, "tarkov-deployed");
        }

    }

    private void JoinRaid(Entity<TarkovRaidComponent> raid, List<string> users, TarkovData data)
    {
        // Existing loot, NPCs, radius, phase and expiry are never rebuilt by later departures.
        if (raid.Comp.EndsAt <= _timing.CurTime) return;
        var roster = new List<(string User, string Life, List<TarkovStoredItem> Items)>();
        var positions = new Dictionary<string, EntityCoordinates>();
        var bodies = new Dictionary<string, EntityUid>();
        foreach (var group in users.GroupBy(u => data.Accounts[u].Party == "" ? u : data.Accounts[u].Party))
        {
            var angle = Random.Shared.NextSingle() * MathF.Tau;
            var center = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * raid.Comp.Radius * 0.68f;
            foreach (var user in group)
            {
                if (FindPlayer(user) is not { } body || !Alive(body)) return;
                // Sample the reserved perimeter route. Do not clear tiles or erase objects during late entry.
                EntityCoordinates? spawn = null;
                for (var attempt = 0; attempt < 256; attempt++)
                {
                    var point = center + new Vector2(Random.Shared.Next(-3, 4), Random.Shared.Next(-3, 4));
                    var tile = point.Floored();
                    if (_maps.GetTileRef(raid.Owner, Comp<MapGridComponent>(raid), tile).Tile.IsEmpty
                        || _turf.IsTileBlocked(raid.Owner, tile, CollisionGroup.MobMask)) continue;
                    var at = new Vector2(tile.X + 0.5f, tile.Y + 0.5f);
                    if (positions.Values.Any(p => Vector2.Distance(p.Position, at) < 1f)) continue;
                    spawn = new EntityCoordinates(raid.Owner, at);
                    break;
                }
                if (spawn == null)
                {
                    foreach (var member in users)
                        if (TrySession(member, out var session)) SendState(session, "tarkov-error-landing");
                    return;
                }
                bodies[user] = body;
                positions[user] = spawn.Value;
                roster.Add((user, Guid.NewGuid().ToString("N"), CaptureRoots(body, user, "raid")));
            }
        }
        var error = Write("system", Guid.NewGuid().ToString("N"), "join-raid",
            d => TarkovEconomy.Deploy(d, raid.Comp.Id, roster));
        if (error != null)
        {
            foreach (var user in users)
                if (TrySession(user, out var session)) SendState(session, error);
            return;
        }
        foreach (var member in roster)
        {
            var body = bodies[member.User];
            var pc = Comp<TarkovPlayerComponent>(body);
            pc.Raid = raid.Comp.Id;
            pc.Life = member.Life;
            pc.ExtractAt = TimeSpan.Zero;
            pc.Exit = null;
            pc.ActiveTerminal = null;
            if (!raid.Comp.Participants.Contains(member.User)) raid.Comp.Participants.Add(member.User);
            _transform.SetCoordinates(body, positions[member.User]);
            Log.Info($"Late raid entry: user={member.User}, raid={raid.Comp.Id}, remaining={raid.Comp.EndsAt - _timing.CurTime}");
            Feedback(member.User, TarkovFeedback.Departure);
            if (TrySession(member.User, out var session)) SendState(session, "tarkov-deployed");
        }
    }

    private void ClearPad(EntityUid map, Vector2 center, int radius)
    {
        var grid = Comp<MapGridComponent>(map);
        var tile = new Tile(IoCManager.Resolve<ITileDefinitionManager>()["FloorDirt"].TileId);
        var tiles = new List<(Vector2i, Tile)>();
        for (var x = -radius; x <= radius; x++)
        for (var y = -radius; y <= radius; y++)
            tiles.Add((new Vector2i((int)center.X + x, (int)center.Y + y), tile));
        _maps.SetTiles(map, grid, tiles);
        _biome.ReserveTiles(map, new Box2(center - new Vector2(radius + 1), center + new Vector2(radius + 1)), tiles);
        foreach (var (cell, _) in tiles) _roof.SetRoof((map, grid, null), cell, false);
        foreach (var uid in _lookup.GetEntitiesInRange(new MapCoordinates(center, Comp<MapComponent>(map).MapId), radius + 0.7f))
        {
            if (uid == map || HasComp<MapGridComponent>(uid) || HasComp<TarkovPlayerComponent>(uid)
                || HasComp<TarkovExitComponent>(uid) || HasComp<TarkovItemComponent>(uid) || HasComp<MobStateComponent>(uid) || !TryComp(uid, out TransformComponent? xform) || xform.ParentUid != map)
                continue;
            QueueDel(uid);
        }
    }

    private string? StartExtraction(EntityUid body)
    {
        var player = Comp<TarkovPlayerComponent>(body);
        if (player.Closed || player.Raid == "" || !Alive(body)) return "tarkov-error-raid";
        if (player.ExtractAt != TimeSpan.Zero) return "tarkov-error-extracting";
        var exits = AllEntityQuery<TarkovExitComponent>();
        while (exits.MoveNext(out var uid, out var exit))
        {
            if (exit.Raid != player.Raid || Transform(uid).MapUid != Transform(body).MapUid
                || Vector2.Distance(_transform.GetWorldPosition(uid), _transform.GetWorldPosition(body)) > 2.5f) continue;
            player.Exit = uid;
            player.ExtractAt = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(1, _cfg.GetCVar(TarkovCVars.ExtractionSeconds)));
            player.LastExtractionTick = 0;
            Feedback(player.User, TarkovFeedback.ExtractionStarted);
            return null;
        }
        return "tarkov-error-exit";
    }

    private void CompleteExtraction(EntityUid body)
    {
        if (_repository == null || !TryComp<TarkovPlayerComponent>(body, out var player) || !Alive(body)) return;
        var records = CaptureRoots(body, player.User, "hub");
        var error = Write(player.User, "extract-" + player.Life, "extract", data =>
        {
            var result = TarkovEconomy.Extract(data, player.User, player.Raid, player.Life, Utc);
            if (result != null) return result;
            foreach (var missing in data.Items.Values.Where(i => i.Owner == player.User && i.Location == "raid")) missing.Location = "lost";
            foreach (var record in records) data.Items[record.Id] = record;
            return null;
        });
        if (error != null) return;
        player.Raid = "";
        player.Exit = null;
        player.ExtractAt = TimeSpan.Zero;
        _transform.SetCoordinates(body, HubCoordinates());
        Feedback(player.User, TarkovFeedback.Extracted);
        if (TrySession(player.User, out var session)) SendState(session, "tarkov-extracted", "stash");
    }

    private void OnExitInteract(Entity<TarkovExitComponent> ent, ref InteractHandEvent args)
    {
        if (!args.Handled && ActivateExit(args.User)) args.Handled = true;
    }

    private void OnExitUsing(Entity<TarkovExitComponent> ent, ref InteractUsingEvent args)
    {
        if (!args.Handled && ActivateExit(args.User)) args.Handled = true;
    }

    private void OnExitActivate(Entity<TarkovExitComponent> ent, ref ActivateInWorldEvent args)
    {
        if (!args.Handled && ActivateExit(args.User)) args.Handled = true;
    }

    private bool ActivateExit(EntityUid user)
    {
        if (!Enabled || !TryComp<TarkovPlayerComponent>(user, out var player)) return false;
        var error = StartExtraction(user);
        if (TrySession(player.User, out var session)) SendState(session, error ?? "tarkov-extract-started");
        return true;
    }

    private void OnPlayerDamage(Entity<TarkovPlayerComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || ent.Comp.Closed || ent.Comp.Raid == "") return;
        if (ent.Comp.ExtractAt != TimeSpan.Zero) Feedback(ent.Comp.User, TarkovFeedback.ExtractionCancelled);
        ent.Comp.ExtractAt = TimeSpan.Zero;
        ent.Comp.Exit = null;
        if (TryComp<TarkovPlayerComponent>(args.Origin, out var attacker) && attacker.Raid == ent.Comp.Raid)
        {
            var credit = EnsureComp<TarkovCombatCreditComponent>(ent);
            credit.Attacker = attacker.User;
            credit.Life = attacker.Life;
            credit.Raid = attacker.Raid;
            credit.LastDamage = _timing.CurTime;
        }
    }

    private void OnPlayerState(Entity<TarkovPlayerComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead && !ent.Comp.Closed) CloseLife(ent);
    }

    private void OnRaidBodyShutdown(Entity<TarkovPlayerComponent> ent, ref ComponentShutdown args)
    {
        // Deletion without a MobState death must neither pin an empty raid nor restore a lost raid loadout in the hub.
        if (Enabled && !ent.Comp.Closed && ent.Comp.Raid != "") CloseLife(ent);
    }

    private void CloseLife(EntityUid body)
    {
        if (_repository == null || !TryComp<TarkovPlayerComponent>(body, out var player) || player.Closed) return;
        player.Closed = true;
        Feedback(player.User, TarkovFeedback.Death);
        var credit = CompOrNull<TarkovCombatCreditComponent>(body);
        Write(player.User, "death-" + player.Life, "death", data =>
        {
            if (!data.Accounts.TryGetValue(player.User, out var account) || account.Life != player.Life) return "tarkov-error-raid";
            if (credit != null && credit.Attacker != "" && credit.Raid == player.Raid && _timing.CurTime - credit.LastDamage < TimeSpan.FromSeconds(90))
                TarkovEconomy.RecordKill(data, credit.Attacker, player.User, player.Raid, credit.Life, Utc);
            TarkovEconomy.LoseLife(data, player.User);
            return null;
        });
        _pendingJoins.Add(player.User);
    }

    private void LoseAllRaids()
    {
        var raids = AllEntityQuery<TarkovRaidComponent>();
        while (raids.MoveNext(out var uid, out var raid))
        {
            foreach (var user in raid.Participants)
                if (FindPlayer(user) is { } body && Comp<TarkovPlayerComponent>(body).Raid == raid.Id) CloseLife(body);
            QueueDel(uid);
        }
    }
}
