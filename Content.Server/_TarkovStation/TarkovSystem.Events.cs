// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Server._TarkovStation.Persistence;
using Content.Shared._TarkovStation;
using Content.Shared._TarkovStation.Components;
using Content.Shared._TarkovStation.Prototypes;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.Audio;
using Robust.Shared.Map;

namespace Content.Server._TarkovStation;

public sealed partial class TarkovSystem
{
    private void PrepareRaidEvent(Entity<TarkovRaidComponent> raid)
    {
        var duration = Math.Max(30, _cfg.GetCVar(TarkovCVars.RaidSeconds));
        // Deterministic 4..7 minute window in a normal 15 minute raid; short QA raids scale with it.
        raid.Comp.EventAt = _timing.CurTime + TimeSpan.FromSeconds(duration * (0.27 + raid.Comp.Seed % 20 / 100.0));
        var angle = raid.Comp.EventKind == TarkovRaidEventKind.Airdrop
            ? MathF.PI / 2 + raid.Comp.Seed % 2 * MathF.PI : raid.Comp.Seed % 4 * MathF.PI / 2;
        raid.Comp.EventPosition = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * raid.Comp.Radius
            * (raid.Comp.EventKind == TarkovRaidEventKind.Airdrop ? 0.58f : 0.68f);
        if (raid.Comp.EventKind == TarkovRaidEventKind.Airdrop)
            ClearPad(raid, raid.Comp.EventPosition, 2);
    }

    private static string EventSector(Vector2 position)
    {
        if (MathF.Abs(position.X) > MathF.Abs(position.Y) * 1.5f) return position.X > 0 ? "ts-sector-east" : "ts-sector-west";
        if (MathF.Abs(position.Y) > MathF.Abs(position.X) * 1.5f) return position.Y > 0 ? "ts-sector-north" : "ts-sector-south";
        return position.Y > 0 ? position.X > 0 ? "ts-sector-northeast" : "ts-sector-northwest"
            : position.X > 0 ? "ts-sector-southeast" : "ts-sector-southwest";
    }

    private void AnnounceRaidEvent(Entity<TarkovRaidComponent> raid, string suffix)
    {
        var message = "ts-event-" + TarkovRaidPlan.EventKey(raid.Comp.EventKind) + "-" + suffix;
        foreach (var user in raid.Comp.Participants)
        {
            if (FindPlayer(user) is not { } body || Comp<TarkovPlayerComponent>(body).Raid != raid.Comp.Id) continue;
            Feedback(user, TarkovFeedback.RaidEvent, message, EventSector(raid.Comp.EventPosition));
        }
    }

    private void UpdateRaidEvent(Entity<TarkovRaidComponent> raid)
    {
        if (raid.Comp.EventStage == 3 || _timing.CurTime >= raid.Comp.EndsAt) return;
        if (raid.Comp.EventStage == 0 && _timing.CurTime >= raid.Comp.EventAt - TimeSpan.FromSeconds(45))
        {
            raid.Comp.EventStage = 1;
            if (raid.Comp.EventKind == TarkovRaidEventKind.Airdrop)
                raid.Comp.EventMarker = Spawn("TarkovStationAirdropSignal", new EntityCoordinates(raid, raid.Comp.EventPosition));
            AnnounceRaidEvent(raid, "warning");
        }
        if (raid.Comp.EventStage == 1 && _timing.CurTime >= raid.Comp.EventAt)
        {
            if (raid.Comp.EventKind == TarkovRaidEventKind.Migration)
            {
                if (!SpawnMigration(raid))
                {
                    // Never materialize attackers beside a player camping a migration entrance.
                    raid.Comp.EventAt += TimeSpan.FromSeconds(10);
                    return;
                }
            }
            else
            {
                if (raid.Comp.EventMarker is { } signal) QueueDel(signal);
                var visual = Spawn("TarkovStationAirdropDescending", new EntityCoordinates(raid, raid.Comp.EventPosition));
                raid.Comp.EventMarker = visual;
                var falling = Comp<TarkovAirdropVisualComponent>(visual);
                falling.LandsAt = _timing.CurTime + TimeSpan.FromSeconds(TarkovAirdropVisualComponent.FallSeconds);
                Dirty(visual, falling);
                _feedbackAudio.PlayPvs(new SoundPathSpecifier("/Audio/Effects/teleport_arrival.ogg"), visual,
                    AudioParams.Default.WithVolume(-3));
            }
            raid.Comp.EventStage = 2;
            AnnounceRaidEvent(raid, "started");
        }
        if (raid.Comp.EventStage != 2) return;
        if (raid.Comp.EventKind == TarkovRaidEventKind.Airdrop)
        {
            if (raid.Comp.EventMarker is not { } marker || !TryComp<TarkovAirdropVisualComponent>(marker, out var falling)
                || _timing.CurTime < falling.LandsAt) return;
            var crate = Spawn("TarkovStationAirdropCrate", new EntityCoordinates(raid, raid.Comp.EventPosition));
            TagTree(crate, false, raid.Comp.Id);
            var catalogue = _proto.EnumeratePrototypes<TarkovGoodsPrototype>().ToDictionary(g => g.ID);
            var random = new Random(raid.Comp.Seed ^ 0x4A1D);
            foreach (var goods in TarkovLootRoller.Roll(_proto.Index<TarkovLootTablePrototype>("TarkovStationAirdropLoot"), catalogue, random,
                TarkovRaidConditions.LootBudget(raid.Comp.DayPhase)))
            {
                var item = Spawn(goods.Product, Transform(crate).Coordinates);
                TagTree(item, false, raid.Comp.Id);
                _entityStorage.Insert(item, crate);
            }
            _feedbackAudio.PlayPvs(new SoundPathSpecifier("/Audio/Effects/metal_thud1.ogg"), crate);
            QueueDel(marker);
            raid.Comp.EventMarker = crate;
            raid.Comp.EventStage = 3;
            AnnounceRaidEvent(raid, "finished");
        }
        else
        {
            var query = AllEntityQuery<TarkovMigrationComponent, MobStateComponent>();
            var living = false;
            while (query.MoveNext(out var uid, out var migrant, out var state))
            {
                if (migrant.RaidMap != raid.Owner || state.CurrentState == MobState.Dead) continue;
                if (migrant.Waypoint >= migrant.Route.Length && !NearRaidPlayer(raid, _transform.GetWorldPosition(uid), 12))
                    QueueDel(uid);
                else living = true;
            }
            if (!living)
            {
                raid.Comp.EventStage = 3;
                AnnounceRaidEvent(raid, "finished");
            }
        }
    }

    private bool SpawnMigration(Entity<TarkovRaidComponent> raid)
    {
        var radius = raid.Comp.Radius * 0.68f;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var angle = raid.Comp.Seed % 4 * MathF.PI / 2 + attempt * MathF.PI / 4;
            var start = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            if (NearRaidPlayer(raid, start, LandingNpcDistance + 3)) continue;
            var route = Enumerable.Range(1, 8).Select(step =>
            {
                var targetAngle = angle + step * MathF.PI / 8;
                return new Vector2(MathF.Cos(targetAngle), MathF.Sin(targetAngle)) * radius;
            }).ToArray();
            var count = Math.Clamp(raid.Comp.Radius / 12 + (int)raid.Comp.DayPhase, 3, 8);
            for (var i = 0; i < count; i++)
            {
                var position = start + new Vector2((i % 2) * 0.7f, (i / 2) * 0.6f);
                var mob = Spawn("TarkovStationMigratingFauna", new EntityCoordinates(raid, position));
                var migration = Comp<TarkovMigrationComponent>(mob);
                migration.RaidMap = raid;
                migration.Route = route;
            }
            raid.Comp.EventPosition = start;
            return true;
        }
        return false;
    }

    private bool NearRaidPlayer(EntityUid map, Vector2 point, float distance)
    {
        var players = AllEntityQuery<TarkovPlayerComponent, TransformComponent>();
        while (players.MoveNext(out _, out var player, out var transform))
            if (!player.Closed && transform.MapUid == map
                && Vector2.DistanceSquared(point, _transform.GetWorldPosition(transform)) < distance * distance) return true;
        return false;
    }

    public bool MigrationDestination(EntityUid owner, out EntityCoordinates destination)
    {
        destination = default;
        if (!TryComp<TarkovMigrationComponent>(owner, out var migration) || !Alive(owner) || Deleted(migration.RaidMap)) return false;
        if (migration.Waypoint >= migration.Route.Length)
        {
            return false;
        }
        destination = new EntityCoordinates(migration.RaidMap, migration.Route[migration.Waypoint]);
        return true;
    }

    public void AdvanceMigration(EntityUid owner)
    {
        if (TryComp<TarkovMigrationComponent>(owner, out var migration)) migration.Waypoint++;
    }
}
