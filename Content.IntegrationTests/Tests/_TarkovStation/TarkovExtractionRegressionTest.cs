// SPDX-License-Identifier: AGPL-3.0-or-later
#nullable enable
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Client._TarkovStation;
using Content.Goobstation.Common.CCVar;
using Content.Server._TarkovStation;
using Content.Server.GameTicking;
using Content.Shared._TarkovStation;
using Content.Shared._TarkovStation.Components;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.CCVar;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Maps;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Preferences;
using Content.Shared.Projectiles;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.IntegrationTests.Tests._TarkovStation;

[TestFixture]
public sealed class TarkovExtractionRegressionTest
{
    [Test]
    public async Task FullAmmunitionBagExtractsAndGuardsFinishCriticalTargets()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            InLobby = true, Map = "TarkovStationHub", Fresh = true, Destructive = true,
        });
        var server = pair.Server;
        var client = pair.Client;
        var mode = server.System<TarkovSystem>();
        var user = client.User!.Value.ToString();
        await server.WaitPost(() =>
        {
            server.CfgMan.SetCVar(TarkovCVars.Enabled, true);
            server.CfgMan.SetCVar(TarkovCVars.QueueSeconds, 3);
            server.CfgMan.SetCVar(TarkovCVars.ExtractionSeconds, 1);
            server.CfgMan.SetCVar(TarkovCVars.TestBots, true);
            server.CfgMan.SetCVar(GoobCVars.DisablePathfinding, false);
            server.CfgMan.SetCVar(CCVars.GameLobbyFallbackEnabled, false);
            var ticker = server.System<GameTicker>();
            ticker.SetGamePreset("TarkovStation");
            ticker.StartRound(true);
        });
        await pair.RunTicksSync(60);
        async Task Request(TarkovAction action, string id = "", string extra = "")
        {
            var page = TarkovServiceAccess.PageFor(action);
            if (page != "")
                await server.WaitPost(() =>
                {
                    var em = server.EntMan;
                    var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                    var terminal = em.EntityQuery<TarkovTerminalComponent>().First(t => t.Page == page).Owner;
                    var at = em.GetComponent<TransformComponent>(terminal).Coordinates;
                    em.System<SharedTransformSystem>().SetCoordinates(body, new EntityCoordinates(at.EntityId, at.Position + (at.Position.Y < 0 ? Vector2.UnitY : -Vector2.UnitY)));
                    em.EventBus.RaiseLocalEvent(terminal, new InteractHandEvent(body, terminal));
                });
            await client.WaitPost(() => client.System<TarkovClientSystem>().Send(new TarkovRequestEvent
            {
                Action = action, Id = id, Extra = extra, AcceptedRules = true,
                Profile = action == TarkovAction.Create ? HumanoidCharacterProfile.DefaultWithSpecies("Human").WithName("Добыча Проверка") : null,
            }));
            await pair.RunTicksSync(15);
        }
        await Request(TarkovAction.AcceptRules);
        await Request(TarkovAction.Create, "Scavengers", "Assault");
        await server.WaitAssertion(() => Assert.That(mode.Admin(new[] { "partner", user }), Is.EqualTo("tarkov-success")));
        await Request(TarkovAction.Ready);
        for (var i = 0; i < 100 && mode.Repository!.Read().Accounts[user].Location != "raid"; i++)
            await pair.RunTicksSync(30);
        Assert.That(mode.Repository!.Read().Accounts[user].Location, Is.EqualTo("raid"));
        EntityUid guard = default;
        EntityUid victim = default;
        EntityUid guardGun = default;
        await server.WaitPost(() =>
        {
            var em = server.EntMan;
            var map = em.EntityQuery<TarkovRaidComponent>().Single().Owner;
            var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
            em.EnsureComponent<GodmodeComponent>(body);
            em.System<SharedTransformSystem>().SetCoordinates(body, new EntityCoordinates(map, new Vector2(25, 0)));
            foreach (var mob in em.EntityQuery<MobStateComponent>().ToArray())
                if (!em.HasComponent<TarkovPlayerComponent>(mob.Owner) && em.GetComponent<TransformComponent>(mob.Owner).MapUid == map)
                    em.QueueDeleteEntity(mob.Owner);
            foreach (var helper in em.EntityQuery<TarkovTestBotComponent>())
                em.System<SharedTransformSystem>().SetCoordinates(helper.Owner, new EntityCoordinates(map, new Vector2(25, 2)));
            Assert.That(em.EntityQuery<MetaDataComponent>().Where(m => em.GetComponent<TransformComponent>(m.Owner).MapUid == map)
                .Any(m => m.EntityPrototype?.ID.Contains("Poster", StringComparison.OrdinalIgnoreCase) == true), Is.False);
            var tile = server.Resolve<ITileDefinitionManager>()["FloorDirt"];
            var tiles = (from x in Enumerable.Range(-15, 15) from y in Enumerable.Range(-4, 9)
                select (new Robust.Shared.Maths.Vector2i(x, y), new Tile(tile.TileId))).ToList();
            em.System<SharedMapSystem>().SetTiles(map, em.GetComponent<MapGridComponent>(map), tiles);
            foreach (var transform in em.EntityQuery<TransformComponent>().ToArray())
                if (transform.ParentUid == map && transform.Anchored && transform.LocalPosition.X is > -15 and < 0 && Math.Abs(transform.LocalPosition.Y) < 4)
                    em.QueueDeleteEntity(transform.Owner);
            guard = em.SpawnEntity("TarkovStationRaidGuardPatrol", new EntityCoordinates(map, new Vector2(-10, 0)));
            victim = em.SpawnEntity("MobHuman", new EntityCoordinates(map, new Vector2(-7.5f, 0)));
            em.System<DamageableSystem>().ChangeDamage(victim, new DamageSpecifier { DamageDict = { ["Piercing"] = 170 } },
                ignoreResistances: true, targetPart: TargetBodyPart.Chest);
            Assert.That(em.GetComponent<MobStateComponent>(victim).CurrentState, Is.EqualTo(MobState.HardCritical));
            guardGun = em.System<SharedHandsSystem>().GetActiveItem(guard)!.Value;
        });
        var killed = false;
        for (var i = 0; i < 60 && !killed; i++)
        {
            await pair.RunTicksSync(15);
            await server.WaitAssertion(() => killed = server.EntMan.GetComponent<MobStateComponent>(victim).CurrentState == MobState.Dead);
        }
        Assert.That(killed, Is.True, "The guard must continue attacking a hard-critical target until death");
        await server.WaitAssertion(() =>
        {
            var em = server.EntMan;
            var gun = em.GetComponent<GunComponent>(guardGun);
            Assert.That(gun.MinAngleModified.Degrees, Is.GreaterThanOrEqualTo(6));
            Assert.That(gun.MaxAngleModified.Degrees, Is.GreaterThanOrEqualTo(14));
            Assert.That(gun.FireRateModified, Is.LessThanOrEqualTo(4));
            Assert.That(em.System<SharedHandsSystem>().TryDrop(guard, guardGun), Is.True);
            Assert.That(gun.MinAngleModified, Is.EqualTo(gun.MinAngle), "Looted weapons must regain their normal player accuracy");
            var round = em.SpawnEntity("BulletLightRifle", em.GetComponent<TransformComponent>(guard).Coordinates);
            var projectile = em.GetComponent<ProjectileComponent>(round);
            projectile.Shooter = guard;
            projectile.Weapon = guardGun;
            em.DeleteEntity(guard);
            Assert.That(projectile.Shooter, Is.Null, "A round may outlive its deleted NPC shooter without breaking PVS");
            em.DeleteEntity(guardGun);
            Assert.That(projectile.Weapon, Is.Null, "A deleted weapon cannot remain as a dangling projectile reference");
            em.QueueDeleteEntity(round);
        });
        EntityUid bag = default;
        await server.WaitAssertion(() =>
        {
            var em = server.EntMan;
            var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
            var at = em.GetComponent<TransformComponent>(body).Coordinates;
            var inventory = em.System<InventorySystem>();
            Assert.That(inventory.TryUnequip(body, "back", silent: true, force: true), Is.True);
            bag = em.SpawnEntity("ClothingBackpackDuffel", at);
            Assert.That(inventory.TryEquip(body, bag, "back", silent: true, force: true), Is.True);
            var storage = em.System<SharedStorageSystem>();
            var guns = em.System<SharedGunSystem>();
            for (var i = 0; i < 16; i++)
            {
                var box = em.SpawnEntity("MagazineBoxLightRifleBig", at);
                var ammo = new TakeAmmoEvent(200, new(), at, body);
                em.EventBus.RaiseLocalEvent(box, ammo);
                Assert.That(ammo.Ammo.Count, Is.EqualTo(200));
                foreach (var (cartridge, _) in ammo.Ammo)
                    Assert.That(guns.TryBallisticInsert((box, em.GetComponent<BallisticAmmoProviderComponent>(box)), cartridge!.Value, body, suppressInsertionSound: true), Is.True);
                Assert.That(storage.Insert(bag, box, out _, body, playSound: false), Is.True, "All ammunition boxes must fit the actual bag grid");
            }
            var exit = em.EntityQuery<TarkovExitComponent>().First().Owner;
            em.System<SharedTransformSystem>().SetCoordinates(body, em.GetComponent<TransformComponent>(exit).Coordinates);
            var partner = em.EntityQuery<TarkovTestBotComponent>().Single(b => b.OwnerUser == user && !b.Target).Owner;
            em.System<Content.Shared.Mobs.Systems.MobStateSystem>().ChangeMobState(partner, MobState.HardCritical);
        });
        await Request(TarkovAction.Extract);
        await pair.RunTicksSync(90);
        Assert.That(mode.Repository.Read().Accounts[user].Location, Is.EqualTo("hub"), "A filled bag must not freeze extraction at one second");
        Assert.That(mode.Repository.Read().Accounts.Values.All(a => a.Location == "hub"), Is.True,
            "A downed test helper must not keep an abandoned raid alive after its owner extracted");
        var stored = mode.Repository.Read().Items.Values.Single(i => i.Owner == user && i.Prototype == "ClothingBackpackDuffel" && i.Parent == "");
        TestContext.Progress.WriteLine($"Full ammunition bag snapshot: {stored.Snapshot.Length} chars");
        Assert.That(stored.Snapshot.Length, Is.GreaterThan(524288), "This regression must exercise the former snapshot-size failure");
        await Request(TarkovAction.Deposit, stored.Id);
        await Request(TarkovAction.Withdraw, stored.Id);
        await server.WaitAssertion(() =>
        {
            var em = server.EntMan;
            var restored = em.EntityQuery<TarkovItemComponent>().Single(i => i.Id == stored.Id).Owner;
            var boxes = em.GetComponent<StorageComponent>(restored).Container.ContainedEntities;
            Assert.That(boxes.Count, Is.EqualTo(16));
            foreach (var box in boxes)
            {
                var ammo = new GetAmmoCountEvent(); em.EventBus.RaiseLocalEvent(box, ref ammo);
                Assert.That(ammo.Count, Is.EqualTo(200), "A large snapshot must preserve every cartridge across stash restore");
            }
        });
        await pair.CleanReturnAsync();
    }
}
