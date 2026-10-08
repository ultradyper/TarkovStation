// SPDX-License-Identifier: AGPL-3.0-or-later
#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Client._TarkovStation;
using Content.Server._TarkovStation;
using Content.Shared._TarkovStation;
using Content.Shared._TarkovStation.Components;
using Content.Shared.CCVar;
using Content.Shared.Preferences;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Server.GameTicking;
using Robust.Shared.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared;
using Content.Shared.Inventory;
using Content.Shared.Storage;
using Content.Shared.Interaction;
using Robust.Shared.Utility;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Content.Shared._TarkovStation.Prototypes;
using Content.Shared.Roles;
using Content.Shared.Stacks;
using Content.Goobstation.Common.CCVar;
using Content.Shared.Hands.EntitySystems;
using Robust.Shared.Log;
using Serilog.Events;
using System.Collections.Concurrent;
using Content.Shared.Procedural;
using Content.Shared.Light.Components;
using Robust.Shared.Network;
using Robust.UnitTesting;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Damage;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Chemistry.EntitySystems;

namespace Content.IntegrationTests.Tests._TarkovStation;

[TestFixture]
public sealed class TarkovNativeTest
{
    [Test]
    public async Task NetworkOnboardingStashRaidExtractionAndWipe()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            InLobby = true, Map = "TarkovStationHub", Fresh = true, Destructive = true,
        });
        try
        {
            var server = pair.Server;
            var client = pair.Client;
            var mode = server.System<TarkovSystem>();
            var runtimeErrors = new RuntimeErrors();
            await server.WaitPost(() => server.Resolve<ILogManager>().RootSawmill.AddHandler(runtimeErrors));
            await client.WaitPost(() => client.Resolve<ILogManager>().RootSawmill.AddHandler(runtimeErrors));
            var ticker = server.System<GameTicker>();
            var user = client.User!.Value.ToString();
            foreach (var instance in new RobustIntegrationTest.IntegrationInstance[] { server, client })
                await instance.WaitAssertion(() =>
                {
                    // Validate composed prototypes. Full entity schemas are server-owned (HTN/food effects have client stubs).
                    // Shared mode prototypes and gear are validated on both sides.
                    var pm = instance.ProtoMan;
                    var serial = instance.Resolve<ISerializationManager>();
                    var prototypes = pm.EnumeratePrototypes<TarkovFactionPrototype>().Cast<IPrototype>()
                        .Concat(pm.EnumeratePrototypes<TarkovKitPrototype>())
                        .Concat(pm.EnumeratePrototypes<TarkovGoodsPrototype>())
                        .Concat(pm.EnumeratePrototypes<TarkovLootTablePrototype>())
                        .Concat(pm.EnumeratePrototypes<EntityPrototype>().Where(p => p.ID.StartsWith("TarkovStation") && instance == server))
                        .Concat(pm.EnumeratePrototypes<StartingGearPrototype>().Where(p => p.ID.StartsWith("TarkovStation")));
                    foreach (var prototype in prototypes)
                    {
                        Assert.That(pm.TryGetMapping(prototype.GetType(), prototype.ID, out var node), Is.True);
                        var composed = (MappingDataNode)node!.Copy();
                        composed.Remove("type");
                        var errors = serial.ValidateNode(prototype.GetType(), composed).GetErrors().Select(e => e.ErrorReason);
                        Assert.That(errors, Is.Empty, prototype.ID);
                    }
                });
            await server.WaitPost(() =>
            {
                server.CfgMan.SetCVar(CVars.NetPVS, true);
                // The fork's general test pool disables navigation for entity-validation performance.
                server.CfgMan.SetCVar(GoobCVars.DisablePathfinding, false);
                server.CfgMan.SetCVar(TarkovCVars.Enabled, true);
                server.CfgMan.SetCVar(TarkovCVars.QueueSeconds, 3);
                server.CfgMan.SetCVar(TarkovCVars.ExtractionSeconds, 1);
                server.CfgMan.SetCVar(TarkovCVars.TestBots, true);
                server.CfgMan.SetCVar(CCVars.GameLobbyFallbackEnabled, false);
                ticker.SetGamePreset("TarkovStation");
                ticker.StartRound(true);
            });
            await pair.RunTicksSync(60);
            Assert.That(mode.Hub, Is.Not.Null);
            Assert.That(ticker.CurrentPreset?.ID, Is.EqualTo("TarkovStation"));
            async Task OpenService(string accountUser, TarkovAction action)
            {
                var page = TarkovServiceAccess.PageFor(action);
                if (page == "") return;
                await server.WaitPost(() =>
                {
                    var em = server.EntMan;
                    var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == accountUser && !p.Closed).Owner;
                    var terminal = em.EntityQuery<TarkovTerminalComponent>().First(t => t.Page == page).Owner;
                    var at = em.GetComponent<TransformComponent>(terminal).Coordinates;
                    // Upper-wall terminals are approached from the south; the departure console from the north.
                    var offset = new System.Numerics.Vector2(accountUser == user ? 0 : -1, at.Position.Y < 0 ? 1 : -1);
                    em.System<SharedTransformSystem>().SetCoordinates(body, new Robust.Shared.Map.EntityCoordinates(at.EntityId, at.Position + offset));
                    var interaction = new InteractHandEvent(body, terminal);
                    em.EventBus.RaiseLocalEvent(terminal, interaction);
                    Assert.That(interaction.Handled, Is.True, $"Physical {page} terminal must be reachable at {at.Position + offset}");
                });
            }
            async Task Request(TarkovAction action, string id = "", string extra = "", bool expectRejection = false)
            {
                await OpenService(user, action);
                await client.WaitPost(() => client.System<TarkovClientSystem>().Send(new TarkovRequestEvent
                {
                    Action = action, Id = id, Extra = extra, AcceptedRules = true,
                    Profile = action == TarkovAction.Create ? HumanoidCharacterProfile.DefaultWithSpecies("Human").WithName("Альфа Испытатель").WithAge(35).WithFlavorText("Альфа-проверка.") : null,
                }));
                await pair.RunTicksSync(12);
                if (!expectRejection && action != TarkovAction.Refresh)
                    await client.WaitAssertion(() => Assert.That(client.System<TarkovClientSystem>().LastActionMessage,
                        Does.Not.StartWith("tarkov-error"), $"Server rejected action {action}"));
            }
            await Request(TarkovAction.Refresh);
            await Request(TarkovAction.Create, "Scavengers", "Assault", expectRejection: true);
            Assert.That(mode.Repository!.Read().Accounts.ContainsKey(user), Is.False, "Creation cannot bypass rules acknowledgement");
            await Request(TarkovAction.AcceptRules);
            await Request(TarkovAction.Create, "Scavengers", "Assault");
            var data = mode.Repository!.Read();
            Assert.That(data.Accounts.ContainsKey(user), Is.True, "Character request must reach the actual server over the client connection");
            Assert.That(data.Accounts[user].PendingKit, Is.Empty);
            Assert.That(data.Accounts[user].Profile, Does.Contain("age: 35"));
            Assert.That(data.Accounts[user].Profile, Does.Contain("Альфа-проверка."));
            Assert.That(data.Items.Values.Count(i => i.Owner == user && i.Location == "hub" && i.Parent == ""), Is.GreaterThan(2));
            var gun = data.Items.Values.First(i => i.Owner == user && i.Prototype == "WeaponRifleLecter");
            async Task RawBuy()
            {
                await client.WaitPost(() => client.System<TarkovClientSystem>().Send(new TarkovRequestEvent { Action = TarkovAction.Buy, Id = "Wrench" }));
                await pair.RunTicksSync(12);
                Assert.That(mode.Repository.Read().Accounts[user].Balance, Is.EqualTo(2500), "Remote or wrong-service requests cannot spend credits");
            }
            await RawBuy();
            await OpenService(user, TarkovAction.Ready); await RawBuy();
            await OpenService(user, TarkovAction.Buy);
            await server.WaitPost(() =>
            {
                var em = server.EntMan; var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                var current = em.GetComponent<TransformComponent>(body).Coordinates;
                em.System<SharedTransformSystem>().SetCoordinates(body, new Robust.Shared.Map.EntityCoordinates(current.EntityId, current.Position + new System.Numerics.Vector2(12, 12)));
            });
            await RawBuy();
            EntityUid paidStack = default, foundStack = default;
            NetEntity foundStackNet = default;
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                var coordinates = em.GetComponent<TransformComponent>(body).Coordinates;
                paidStack = em.SpawnEntity("Gauze", coordinates);
                foundStack = em.SpawnEntity("Gauze", coordinates);
                var paid = em.AddComponent<TarkovItemComponent>(paidStack); paid.Id = Guid.NewGuid().ToString("N");
                var found = em.AddComponent<TarkovItemComponent>(foundStack); found.Id = Guid.NewGuid().ToString("N"); found.FoundRaid = "fixture-provenance";
                em.Dirty(foundStack, found);
                var stacks = em.System<SharedStackSystem>(); stacks.SetCount(paidStack, 3); stacks.SetCount(foundStack, 3);
                Assert.That(stacks.TryMergeStacks(paidStack, foundStack, out _), Is.False, "Purchased units cannot become raid finds by merging");
                Assert.That(em.GetComponent<StackComponent>(paidStack).Count, Is.EqualTo(3));
                foundStackNet = em.GetNetEntity(foundStack);
            });
            await pair.RunTicksSync(30);
            await client.WaitAssertion(() =>
            {
                var local = client.EntMan.GetEntity(foundStackNet);
                Assert.That(client.EntMan.GetComponent<TarkovItemComponent>(local).FoundRaid, Is.EqualTo("fixture-provenance"),
                    "Client prediction must receive the same provenance that the server enforces");
            });
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan; var stacks = em.System<SharedStackSystem>();
                var paid = em.GetComponent<TarkovItemComponent>(paidStack);
                var found = em.GetComponent<TarkovItemComponent>(foundStack);
                found.FoundRaid = ""; paid.Emergency = true;
                Assert.That(stacks.TryMergeStacks(paidStack, foundStack, out _), Is.False, "Loaned units cannot be laundered into saleable stacks");
                paid.Emergency = false;
                Assert.That(stacks.TryMergeStacks(paidStack, foundStack, out var moved), Is.True);
                Assert.That(moved, Is.EqualTo(3));
                found.FoundRaid = "fixture-provenance";
                var split = em.System<Content.Server.Stack.StackSystem>().Split(
                    new Entity<StackComponent?>(foundStack, em.GetComponent<StackComponent>(foundStack)), 2,
                    em.GetComponent<TransformComponent>(foundStack).Coordinates);
                Assert.That(split, Is.Not.Null);
                Assert.That(em.GetComponent<TarkovItemComponent>(split!.Value).FoundRaid, Is.EqualTo("fixture-provenance"));
                Assert.That(em.GetComponent<TarkovItemComponent>(split.Value).Id, Is.Not.EqualTo(found.Id));
                em.QueueDeleteEntity(split.Value); em.QueueDeleteEntity(foundStack);
            });
            await Request(TarkovAction.CancelReady);
            await server.WaitAssertion(() => Assert.That(server.EntMan.EntityQuery<TarkovPlayerComponent>()
                .Single(p => p.User == user && !p.Closed).QueueNotice, Is.Empty,
                "Cancelling an idle queue must not invent a disconnection or departure warning"));
            var expectedAmmo = 0;
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var uid = em.EntityQuery<TarkovItemComponent>().Single(i => i.Id == gun.Id).Owner;
                em.System<SharedGunSystem>().SetBoltClosed(uid, em.GetComponent<ChamberMagazineAmmoProviderComponent>(uid), true);
                var before = new GetAmmoCountEvent();
                em.EventBus.RaiseLocalEvent(uid, ref before);
                var take = new TakeAmmoEvent(3, new(), em.GetComponent<TransformComponent>(uid).Coordinates, null);
                em.EventBus.RaiseLocalEvent(uid, take);
                foreach (var (ammo, _) in take.Ammo) if (ammo != null) em.QueueDeleteEntity(ammo.Value);
                var after = new GetAmmoCountEvent();
                em.EventBus.RaiseLocalEvent(uid, ref after);
                Assert.That(after.Count, Is.LessThan(before.Count));
                expectedAmmo = after.Count;
            });
            await Request(TarkovAction.Deposit, gun.Id);
            Assert.That(mode.Repository.Read().Items[gun.Id].Value, Is.LessThan(gun.Value),
                "Used magazine ammunition must lower the actual buyback quote");
            Assert.That(mode.Repository.Read().Items[gun.Id].Location, Is.EqualTo("stash"));
            await Request(TarkovAction.Withdraw, gun.Id);
            Assert.That(mode.Repository.Read().Items[gun.Id].Location, Is.EqualTo("hub"));
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var uid = em.EntityQuery<TarkovItemComponent>().Single(i => i.Id == gun.Id).Owner;
                var ammo = new GetAmmoCountEvent();
                em.EventBus.RaiseLocalEvent(uid, ref ammo);
                Assert.That(ammo.Count, Is.EqualTo(expectedAmmo), "Stash restore must not refill a partially empty magazine");
                var body = em.EntityQuery<TarkovPlayerComponent>().Single(i => i.User == user && !i.Closed).Owner;
                var before = em.GetComponent<DamageableComponent>(body).TotalDamage;
                em.System<DamageableSystem>().TryChangeDamage(body, new DamageSpecifier { DamageDict = { ["Blunt"] = 100 } }, ignoreResistances: true);
                Assert.That(em.GetComponent<DamageableComponent>(body).TotalDamage, Is.EqualTo(before), "Hub must reject actual damage");
            });
            await pair.RunTicksSync(30);
            await client.WaitAssertion(() =>
            {
                var em = client.EntMan;
                var body = client.Resolve<Robust.Client.Player.IPlayerManager>().LocalEntity!.Value;
                var uid = em.System<InventorySystem>().GetHandOrInventoryEntities((body, null, null))
                    .Single(e => em.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == "WeaponRifleLecter");
                var ammo = new GetAmmoCountEvent(); em.EventBus.RaiseLocalEvent(uid, ref ammo);
                Assert.That(ammo.Count, Is.EqualTo(expectedAmmo), "PVS-enabled client must receive the restored magazine and its ammunition");
            });
            var bag = mode.Repository.Read().Items.Values.Single(i => i.Owner == user && i.Prototype == "ClothingBackpackSatchel" && i.Parent == "");
            await server.WaitPost(() =>
            {
                var em = server.EntMan;
                var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                var uid = em.EntityQuery<TarkovItemComponent>().Single(i => i.Id == bag.Id).Owner;
                em.System<SharedUserInterfaceSystem>().TryOpenUi(uid, StorageComponent.StorageUiKey.Key, body);
            });
            // Direct deposit from native storage must atomically rewrite the parent snapshot and value.
            string nestedId = "";
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var bagEntity = em.EntityQuery<TarkovItemComponent>().Single(i => i.Id == bag.Id).Owner;
                var nested = em.GetComponent<StorageComponent>(bagEntity).Container.ContainedEntities
                    .First(i => em.HasComponent<TarkovItemComponent>(i));
                nestedId = em.GetComponent<TarkovItemComponent>(nested).Id;
            });
            await Request(TarkovAction.Deposit, nestedId);
            Assert.That(mode.Repository.Read().Items[nestedId].Parent, Is.Empty);
            Assert.That(mode.Repository.Read().Items[nestedId].Location, Is.EqualTo("stash"));
            Assert.That(mode.Repository.Read().Items[bag.Id].Snapshot, Does.Not.Contain(nestedId),
                "Parent snapshot must no longer contain a deposited nested item");
            EntityUid unpriced = default;
            var unpricedId = "";
            await server.WaitPost(() =>
            {
                var em = server.EntMan;
                var body = em.EntityQuery<TarkovPlayerComponent>().Single(pc => pc.User == user && !pc.Closed).Owner;
                var bagEntity = em.EntityQuery<TarkovItemComponent>().Single(item => item.Id == bag.Id).Owner;
                unpriced = em.SpawnEntity("Paper", em.GetComponent<TransformComponent>(body).Coordinates);
                Assert.That(em.System<Content.Shared.Storage.EntitySystems.SharedStorageSystem>().Insert(bagEntity, unpriced, out _, user: body), Is.True);
            });
            await OpenService(user, TarkovAction.Deposit);
            await server.WaitAssertion(() =>
            {
                Assert.That(server.EntMan.TryGetComponent<TarkovItemComponent>(unpriced, out var tracked), Is.True,
                    "Opening the stash must discover bag contents even when the item is absent from the shop");
                unpricedId = tracked!.Id;
            });
            await Request(TarkovAction.Deposit, unpricedId);
            Assert.That(mode.Repository.Read().Items[unpricedId].Location, Is.EqualTo("stash"));
            Assert.That(mode.Repository.Read().Items[unpricedId].Value, Is.Zero);
            Assert.That(mode.Repository.Read().Items[bag.Id].Snapshot, Does.Not.Contain(unpricedId));
            await Request(TarkovAction.Deposit, bag.Id);
            Assert.That(mode.Repository.Read().Items[bag.Id].Snapshot, Does.Not.Contain("actors:"), "A persisted bag cannot retain subscribers from the old body");
            await Request(TarkovAction.Withdraw, bag.Id);
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var uid = em.EntityQuery<TarkovItemComponent>().Single(i => i.Id == bag.Id).Owner;
                Assert.That(em.System<InventorySystem>().TryGetContainingSlot((uid, null, null), out var equippedSlot), Is.True,
                    "A restored backpack must return to its back slot, not occupy a hand");
                Assert.That(equippedSlot!.Name, Is.EqualTo("back"));
            });
            await Request(TarkovAction.Create, "Exiles", "Medic", expectRejection: true);
            Assert.That(mode.Repository.Read().Accounts[user].Branch, Is.EqualTo("Assault"), "Repeated creation cannot grant another kit");
            await Request(TarkovAction.Buy, "DrinkWaterBottleFull");
            var bottle = mode.Repository.Read().Items.Values.Single(i => i.Owner == user && i.Prototype == "DrinkWaterBottleFull" && i.Parent == "");
            await Request(TarkovAction.Withdraw, bottle.Id);
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var uid = em.EntityQuery<TarkovItemComponent>().Single(i => i.Id == bottle.Id).Owner;
                var solutions = em.System<SharedSolutionContainerSystem>();
                Assert.That(solutions.TryGetSolution(uid, "drink", out var solutionEnt, out _), Is.True);
                solutions.SplitSolution(solutionEnt!.Value, 7);
            });
            await Request(TarkovAction.Deposit, bottle.Id);
            await Request(TarkovAction.Withdraw, bottle.Id);
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var uid = em.EntityQuery<TarkovItemComponent>().Single(i => i.Id == bottle.Id).Owner;
                Assert.That(em.System<SharedSolutionContainerSystem>().TryGetSolution(uid, "drink", out _, out var solution), Is.True);
                Assert.That(solution!.Volume.Float(), Is.EqualTo(23), "Native reagent contents must survive stash serialization");
            });
            await server.WaitAssertion(() =>
            {
                var body = server.EntMan.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                Assert.That(server.System<SharedHandsSystem>().CountFreeHands(body), Is.Zero);
            });
            await Request(TarkovAction.Buy, "Wrench");
            var noSpace = mode.Repository.Read().Items.Values.Single(i => i.Owner == user && i.Prototype == "Wrench" && i.Parent == "" && i.Location == "stash");
            await Request(TarkovAction.Withdraw, noSpace.Id, expectRejection: true);
            Assert.That(mode.Repository.Read().Items[noSpace.Id].Location, Is.EqualTo("stash"), "Full hands must leave the purchased item safely in stash");
            await server.WaitAssertion(() => Assert.That(server.EntMan.EntityQuery<TarkovItemComponent>().Any(i => i.Id == noSpace.Id), Is.False,
                "Failed placement must not leave a duplicate world projection"));
            await Request(TarkovAction.Sell, noSpace.Id);
            var beforeBountyBalance = mode.Repository.Read().Accounts[user].Balance;
            await Request(TarkovAction.TestPartner);
            Assert.That(mode.Repository.Read().Accounts.Count, Is.EqualTo(3));
            // Regression for the real player's report: helpers must not leave after their owner cancels/leaves.
            await Request(TarkovAction.Ready);
            await Request(TarkovAction.LeaveParty);
            await pair.RunTicksSync(180);
            Assert.That(mode.Repository.Read().Accounts.Values.All(a => a.Location == "hub" && a.Extractions == 0), Is.True);
            await Request(TarkovAction.Ready);
            await Request(TarkovAction.CancelReady);
            await pair.RunTicksSync(180);
            Assert.That(mode.Repository.Read().Accounts.Values.All(a => a.Location == "hub" && a.Extractions == 0), Is.True);
            // Recreate helper bodies as after process recovery: persisted IDs and budgets must be reused.
            var helperIds = mode.Repository.Read().Accounts.Values.Where(a => a.TestBot).Select(a => a.User).ToArray();
            await server.WaitPost(() =>
            {
                foreach (var pc in server.EntMan.EntityQuery<TarkovPlayerComponent>().Where(p => helperIds.Contains(p.User)).ToArray())
                    server.EntMan.QueueDeleteEntity(pc.Owner);
            });
            await pair.RunTicksSync(6);
            await Request(TarkovAction.TestPartner);
            Assert.That(mode.Repository.Read().Accounts.Count, Is.EqualTo(3));
            Assert.That(mode.Repository.Read().Accounts.Values.Count(a => a.TestOwner == user), Is.EqualTo(2));
            var restoredParty = mode.Repository.Read().Accounts[user].Party;
            Assert.That(restoredParty, Is.Not.Empty, "Preparing existing helpers must restore the party after the owner left it");
            Assert.That(mode.Repository.Read().Accounts.Values.Single(a => a.TestOwner == user && !a.TestTarget).Party, Is.EqualTo(restoredParty));
            // A cancelled or failed generator must keep the complete roster and carried equipment in the hub.
            foreach (var fail in new[] { false, true })
            {
                await Request(TarkovAction.Ready);
                await server.WaitPost(() => mode.Admin(new[] { "raidnow" }));
                await pair.RunTicksSync(1);
                var hold = new TaskCompletionSource<System.Collections.Generic.List<Dungeon>>();
                await server.WaitAssertion(() =>
                {
                    var pending = server.EntMan.EntityQuery<TarkovGenerationComponent>(includePaused: true).Single();
                    pending.Jobs.Add(fail
                        ? Task.FromException<System.Collections.Generic.List<Dungeon>>(new InvalidOperationException("Injected generation failure"))
                        : hold.Task);
                });
                if (!fail)
                {
                    await Request(TarkovAction.CancelReady);
                    hold.SetResult(new());
                }
                await pair.RunTicksSync(60);
                Assert.That(mode.Repository.Read().Accounts.Values.All(a => a.Location == "hub" && a.Extractions == 0), Is.True);
                await server.WaitAssertion(() => Assert.That(server.EntMan.EntityQuery<TarkovGenerationComponent>(includePaused: true), Is.Empty));
            }
            var bounty = mode.Repository.Read().Contracts.Values.Single();
            await Request(TarkovAction.AcceptContract, bounty.Id);
            await Request(TarkovAction.Ready);
            for (var i = 0; i < 100 && mode.Repository.Read().Accounts[user].Location != "raid"; i++)
                await pair.RunTicksSync(30);
            Assert.That(mode.Repository.Read().Accounts[user].Location, Is.EqualTo("raid"), "Native planet/dungeon must generate and deploy the player");
            NetEntity wieldedVirtual = default;
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                Assert.That(em.HasComponent<LightCycleComponent>(em.GetComponent<TransformComponent>(body).MapUid), Is.False,
                    "Raid visibility must not randomly become pitch black during entry");
                Assert.That(em.EntityQuery<Robust.Shared.Map.Components.MapGridComponent>()
                    .Count(g => em.GetComponent<TransformComponent>(g.Owner).MapUid == em.GetComponent<TransformComponent>(body).MapUid), Is.EqualTo(1),
                    "POIs must be patched into the planet rather than overlapping independent physics grids");
                var mined = em.SpawnEntity("SheetSteel10", em.GetComponent<TransformComponent>(body).Coordinates);
                Assert.That(em.GetComponent<TarkovItemComponent>(mined).FoundRaid,
                    Is.EqualTo(em.GetComponent<TarkovPlayerComponent>(body).Raid), "Newly produced raid resources must retain their origin");
                em.QueueDeleteEntity(mined);
                var victim = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == bounty.Target && !p.Closed).Owner;
                em.System<DamageableSystem>().TryChangeDamage(victim, new DamageSpecifier { DamageDict = { ["Blunt"] = 1 } }, origin: body);
                em.System<MobStateSystem>().ChangeMobState(victim, MobState.Dead);
                Assert.That(mode.Repository.Read().Contracts[bounty.Id].Status, Is.EqualTo("proof"));
                Assert.That(mode.Repository.Read().Accounts[user].Balance, Is.EqualTo(beforeBountyBalance), "Killing the target cannot pay before extraction");
                var exit = em.EntityQuery<TarkovExitComponent>().First().Owner;
                em.System<SharedTransformSystem>().SetCoordinates(body, em.GetComponent<TransformComponent>(exit).Coordinates);
                Assert.That(em.System<SharedHandsSystem>().CountFreeHands(body), Is.Zero);
                var held = em.System<SharedHandsSystem>().EnumerateHeld(body).First();
                var activation = new InteractUsingEvent(body, held, exit, em.GetComponent<TransformComponent>(exit).Coordinates);
                em.EventBus.RaiseLocalEvent(exit, activation);
                Assert.That(activation.Handled, Is.True, "The exit must accept a player whose hands carry equipment");
                Assert.That(em.GetComponent<TarkovPlayerComponent>(body).ExtractAt, Is.GreaterThan(TimeSpan.Zero));
                em.System<DamageableSystem>().TryChangeDamage(body,
                    new DamageSpecifier { DamageDict = { ["Blunt"] = 1 } }, ignoreResistances: true);
                Assert.That(em.GetComponent<TarkovPlayerComponent>(body).ExtractAt, Is.EqualTo(TimeSpan.Zero),
                    "Taking damage must interrupt extraction");
                // The successful persistence fixture is isolated from combat; combat is tested separately below.
                em.EnsureComponent<GodmodeComponent>(body);
                // Keep a wielded NPC in the visible map when the player leaves: its virtual hand must not be deleted by client PVS handling.
                var at = em.GetComponent<TransformComponent>(exit).Coordinates;
                var observerGuard = em.SpawnEntity("TarkovStationRaidGuardPatrol", new Robust.Shared.Map.EntityCoordinates(at.EntityId,
                    at.Position + new System.Numerics.Vector2(-2, 1)));
                var factions = em.System<Content.Shared.NPC.Systems.NpcFactionSystem>();
                factions.ClearFactions(observerGuard);
                foreach (var faction in em.GetComponent<Content.Shared.NPC.Components.NpcFactionMemberComponent>(body).Factions)
                    factions.AddFaction(observerGuard, faction.Id);
                var gun = em.System<SharedHandsSystem>().EnumerateHeld(observerGuard).Single(item => em.HasComponent<GunComponent>(item));
                Assert.That(em.System<Content.Shared.Wieldable.SharedWieldableSystem>().TryWield(gun,
                    em.GetComponent<Content.Shared.Wieldable.Components.WieldableComponent>(gun), observerGuard, showMessage: false), Is.True);
                var virtualItem = em.System<SharedHandsSystem>().EnumerateHeld(observerGuard)
                    .Single(item => em.HasComponent<Content.Shared.Inventory.VirtualItem.VirtualItemComponent>(item));
                wieldedVirtual = em.GetNetEntity(virtualItem);
            });
            var cancellationVisible = false;
            for (var i = 0; i < 10 && !cancellationVisible; i++)
            {
                await pair.RunTicksSync(6);
                await client.WaitAssertion(() => cancellationVisible = client.System<TarkovClientSystem>().NoticeText
                    == Robust.Shared.Localization.Loc.GetString("ts-notice-extraction-cancelled"));
            }
            await client.WaitAssertion(() =>
            {
                var actual = client.System<TarkovClientSystem>().NoticeText;
                var expected = Robust.Shared.Localization.Loc.GetString("ts-notice-extraction-cancelled");
                if (actual != expected) Console.Error.WriteLine($"Native extraction notice mismatch: expected={expected}, actual={actual}");
                Assert.That(actual, Is.EqualTo(expected), "A passive extraction snapshot must not overwrite the interruption notice");
            });
            var virtualVisible = false;
            for (var i = 0; i < 20 && !virtualVisible; i++)
            {
                await pair.RunTicksSync(6);
                await client.WaitAssertion(() => virtualVisible = client.EntMan.TryGetEntity(wieldedVirtual, out var local)
                    && client.EntMan.HasComponent<Content.Shared.Inventory.VirtualItem.VirtualItemComponent>(local));
            }
            Assert.That(virtualVisible, Is.True, "The client must actually receive the NPC's networked virtual hand before the PVS transition");
            await server.WaitPost(() =>
            {
                var em = server.EntMan;
                var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                var exit = em.EntityQuery<TarkovExitComponent>().First().Owner;
                var held = em.System<SharedHandsSystem>().EnumerateHeld(body).First();
                em.EventBus.RaiseLocalEvent(exit, new InteractUsingEvent(body, held, exit, em.GetComponent<TransformComponent>(exit).Coordinates));
            });
            // Stress the actual PVS transition: the server finishes extraction and deletes the old map while the client is paused.
            await server.WaitRunTicks(60);
            await server.WaitAssertion(() => Assert.That(server.EntMan.EntityQuery<TarkovRaidComponent>(), Is.Empty));
            await pair.RunTicksSync(60);
            await pair.RunTicksSync(12);
            Assert.That(mode.Repository.Read().Accounts[user].Location, Is.EqualTo("hub"));
            Assert.That(mode.Repository.Read().Accounts[user].Extractions, Is.EqualTo(1));
            Assert.That(mode.Repository.Read().Contracts[bounty.Id].Status, Is.EqualTo("paid"));
            Assert.That(mode.Repository.Read().Accounts[user].Balance, Is.EqualTo(beforeBountyBalance + 500));
            await Request(TarkovAction.Deposit, bottle.Id);
            var npc = bounty.Issuer;
            await Request(TarkovAction.OfferTrade, npc);
            var npcTrade = mode.Repository.Read().Trades.Values.Single(t => t.Status == "open");
            await Request(TarkovAction.AddTradeItem, npcTrade.Id, bottle.Id);
            await Request(TarkovAction.AcceptTrade, npcTrade.Id);
            Assert.That(mode.Repository.Read().Trades[npcTrade.Id].Status, Is.EqualTo("done"), "The solo NPC partner must complete an actual escrow trade");
            Assert.That(mode.Repository.Read().Items[bottle.Id].Owner, Is.EqualTo(npc));
            // Audit the actual native contents, including loaded magazines and batteries, against kit prices.
            await server.WaitAssertion(() => Assert.That(mode.Admin(new[] { "grant", user, "10000" }), Is.EqualTo("tarkov-success")));
            var walletBeforeForbiddenBuy = mode.Repository.Read().Accounts[user].Balance;
            await Request(TarkovAction.Buy, "TarkovStationSignalRecorder", expectRejection: true);
            Assert.That(mode.Repository.Read().Accounts[user].Balance, Is.EqualTo(walletBeforeForbiddenBuy));
            foreach (var kit in server.ProtoMan.EnumeratePrototypes<TarkovKitPrototype>().Where(k => !k.Emergency).ToArray())
            {
                var previousIds = mode.Repository.Read().Items.Keys.ToHashSet();
                await Request(TarkovAction.BuyKit, kit.ID);
                var roots = mode.Repository.Read().Items.Values.Where(i => !previousIds.Contains(i.Id)
                    && i.Owner == user && i.Location == "stash" && i.Parent == "").ToArray();
                Assert.That(roots, Is.Not.Empty, kit.ID);
                Assert.That(roots.Sum(i => i.Value), Is.InRange(1L, kit.Price - 1L), "Kit resale must never fund repeated purchases: " + kit.ID);
                foreach (var root in roots) await Request(TarkovAction.Sell, root.Id);
            }
            var balanceBeforeTrade = mode.Repository.Read().Accounts[user].Balance;
            // A separate real client instance exercises the private wallet and bilateral trade over the network.
            using var second = new RobustIntegrationTest.ClientIntegrationInstance(client.ClientOptions!);
            await second.WaitIdleAsync();
            second.SetConnectTarget(server);
            await second.WaitPost(() => second.Resolve<IClientNetManager>().ClientConnect(null!, 0, "TarkovSecond"));
            for (var i = 0; i < 4; i++) { await pair.RunTicksSync(30); await second.WaitRunTicks(30); }
            async Task SecondRequest(TarkovAction action, string id = "", string extra = "", int amount = 0, string text = "")
            {
                if (second.User is { } secondUser) await OpenService(secondUser.ToString(), action);
                await second.WaitPost(() => second.System<TarkovClientSystem>().Send(new TarkovRequestEvent
                {
                    Action = action, Id = id, Extra = extra, Amount = amount, Text = text, AcceptedRules = true,
                    Profile = action == TarkovAction.Create ? HumanoidCharacterProfile.DefaultWithSpecies("Human").WithName("Второй Испытатель") : null,
                }));
                await pair.RunTicksSync(12); await second.WaitRunTicks(12);
            }
            await SecondRequest(TarkovAction.Refresh);
            await SecondRequest(TarkovAction.AcceptRules);
            await SecondRequest(TarkovAction.Create, "Exiles", "Engineer");
            var other = second.User!.Value.ToString();
            Assert.That(other, Is.Not.EqualTo(user));
            Assert.That(mode.Repository.Read().Accounts.ContainsKey(other), Is.True);
            // Real escrow-backed item delivery and custom acceptance through two authenticated test sessions.
            await SecondRequest(TarkovAction.CreateContract, "TarkovStationEncryptedDrive", extra: "item:1", amount: 100);
            var delivery = mode.Repository.Read().Contracts.Values.Single(c => c.Issuer == other && c.Kind == "item");
            await Request(TarkovAction.AcceptContract, delivery.Id);
            await Request(TarkovAction.DeliverContract, delivery.Id, expectRejection: true);
            var driveId = Guid.NewGuid().ToString("N");
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                var drive = em.SpawnEntity("TarkovStationEncryptedDrive", em.GetComponent<TransformComponent>(body).Coordinates);
                var tag = em.AddComponent<TarkovItemComponent>(drive); tag.Id = driveId; tag.FoundRaid = "fixture-extracted";
                Assert.That(em.System<SharedHandsSystem>().TryPickupAnyHand(body, drive), Is.True);
            });
            await Request(TarkovAction.Deposit, driveId);
            await Request(TarkovAction.DeliverContract, delivery.Id);
            Assert.That(mode.Repository.Read().Contracts[delivery.Id].Status, Is.EqualTo("paid"));
            Assert.That(mode.Repository.Read().Items[driveId].Owner, Is.EqualTo(other));
            await SecondRequest(TarkovAction.CreateContract, extra: "custom:1", amount: 75, text: "Помочь с проверкой связи");
            var custom = mode.Repository.Read().Contracts.Values.Single(c => c.Issuer == other && c.Kind == "custom");
            await Request(TarkovAction.AcceptContract, custom.Id);
            await Request(TarkovAction.ConfirmContract, custom.Id, expectRejection: true);
            await SecondRequest(TarkovAction.ConfirmContract, custom.Id);
            Assert.That(mode.Repository.Read().Contracts[custom.Id].Status, Is.EqualTo("paid"));
            Assert.That(mode.Repository.Read().Accounts[other].Reserved, Is.Zero);
            balanceBeforeTrade = mode.Repository.Read().Accounts[user].Balance;
            var otherBeforeTrade = mode.Repository.Read().Accounts[other].Balance;
            await Request(TarkovAction.Deposit, gun.Id);
            await Request(TarkovAction.OfferTrade, other, expectRejection: true);
            Assert.That(mode.Repository.Read().Trades.Values.Any(t => t.Status == "open"), Is.False,
                "A remote player cannot be locked into an unsolicited trade");
            await OpenService(other, TarkovAction.OfferTrade);
            await Request(TarkovAction.OfferTrade, other);
            await second.WaitRunTicks(12);
            var trade = mode.Repository.Read().Trades.Values.Single(t => t.Status == "open");
            await SecondRequest(TarkovAction.SetTradeMoney, trade.Id, amount: 50);
            await Request(TarkovAction.AddTradeItem, trade.Id, gun.Id);
            await Request(TarkovAction.AcceptTrade, trade.Id);
            await second.WaitRunTicks(12);
            await SecondRequest(TarkovAction.AcceptTrade, trade.Id);
            Assert.That(mode.Repository.Read().Items[gun.Id].Owner, Is.EqualTo(other));
            Assert.That(mode.Repository.Read().Accounts[user].Balance, Is.EqualTo(balanceBeforeTrade + 50));
            Assert.That(mode.Repository.Read().Accounts[other].Balance, Is.EqualTo(otherBeforeTrade - 50));
            await second.WaitPost(() => second.Resolve<IClientNetManager>().ClientDisconnect("Reconnect check"));
            await pair.RunTicksSync(30); await second.WaitRunTicks(30);
            second.SetConnectTarget(server);
            await second.WaitPost(() => second.Resolve<IClientNetManager>().ClientConnect(null!, 0, "TarkovSecond"));
            for (var i = 0; i < 3; i++) { await pair.RunTicksSync(30); await second.WaitRunTicks(30); }
            Assert.That(second.User!.Value.ToString(), Is.EqualTo(other));
            Assert.That(mode.Repository.Read().Accounts[other].PendingKit, Is.Empty);
            Assert.That(mode.Repository.Read().Items[gun.Id].Owner, Is.EqualTo(other));
            await Request(TarkovAction.Invite, other);
            await SecondRequest(TarkovAction.AcceptInvite, user);
            await Request(TarkovAction.Ready);
            await SecondRequest(TarkovAction.Ready);
            for (var i = 0; i < 100 && mode.Repository.Read().Accounts[other].Location != "raid"; i++)
            { await pair.RunTicksSync(30); await second.WaitRunTicks(30); }
            Assert.That(mode.Repository.Read().Accounts[other].Location, Is.EqualTo("raid"));
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var firstBody = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                var secondBody = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == other && !p.Closed).Owner;
                Assert.That(System.Numerics.Vector2.Distance(em.GetComponent<TransformComponent>(firstBody).LocalPosition,
                    em.GetComponent<TransformComponent>(secondBody).LocalPosition), Is.LessThan(6), "An accepted party must spawn together");
                em.System<DamageableSystem>().TryChangeDamage(secondBody, new DamageSpecifier { DamageDict = { ["Blunt"] = 1 } }, origin: firstBody);
                em.System<MobStateSystem>().ChangeMobState(secondBody, MobState.Dead);
            });
            await pair.RunTicksSync(60); await second.WaitRunTicks(60);
            Assert.That(mode.Repository.Read().Accounts[other].Location, Is.EqualTo("hub"));
            Assert.That(mode.Repository.Read().Accounts[other].PendingKit, Is.Empty);
            Assert.That(mode.Repository.Read().Items[gun.Id].Location, Is.EqualTo("stash"), "Death must keep previously stored equipment");
            Assert.That(mode.Repository.Read().Items.Values.Any(i => i.Owner == other && i.Location == "lost"), Is.True);
            Assert.That(mode.Repository.Read().Accounts[user].Kills, Is.EqualTo(1), "Friendly fire is legal, and NPC test kills do not inflate the score");
            // A participant who died may return to the existing raid without generating fresh loot or a new timer.
            var activeRaidId = mode.Repository.Read().Accounts[user].Raid;
            var raidSequence = mode.Repository.Read().RaidSequence;
            TimeSpan raidExpiry = default;
            TarkovDayPhase raidPhase = default;
            EntityUid raidMap = default;
            EntityUid[] cacheEntities = Array.Empty<EntityUid>();
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var raid = em.EntityQuery<TarkovRaidComponent>().Single();
                raidMap = raid.Owner; raidExpiry = raid.EndsAt; raidPhase = raid.DayPhase;
                cacheEntities = em.EntityQuery<Content.Shared.Storage.Components.EntityStorageComponent>()
                    .Where(c => em.GetComponent<TransformComponent>(c.Owner).MapUid == raidMap).Select(c => c.Owner).ToArray();
            });
            await SecondRequest(TarkovAction.Ready);
            for (var i = 0; i < 100 && mode.Repository.Read().Accounts[other].Location != "raid"; i++)
            { await pair.RunTicksSync(30); await second.WaitRunTicks(30); }
            Assert.That(mode.Repository.Read().Accounts[other].Raid, Is.EqualTo(activeRaidId));
            Assert.That(mode.Repository.Read().RaidSequence, Is.EqualTo(raidSequence));
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var raid = em.EntityQuery<TarkovRaidComponent>().Single();
                Assert.That(raid.Owner, Is.EqualTo(raidMap));
                Assert.That(raid.EndsAt, Is.EqualTo(raidExpiry));
                Assert.That(raid.DayPhase, Is.EqualTo(raidPhase));
                Assert.That(em.EntityQuery<Content.Shared.Storage.Components.EntityStorageComponent>()
                    .Where(c => em.GetComponent<TransformComponent>(c.Owner).MapUid == raidMap).Select(c => c.Owner),
                    Is.EquivalentTo(cacheEntities), "Late entry cannot regenerate the caches");
            });
            EntityUid bodyBeforeReconnect = default;
            var positionBeforeReconnect = System.Numerics.Vector2.Zero;
            var lifeBeforeReconnect = mode.Repository.Read().Accounts[other].Life;
            await server.WaitPost(() =>
            {
                var em = server.EntMan;
                bodyBeforeReconnect = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == other && !p.Closed).Owner;
                em.EnsureComponent<GodmodeComponent>(bodyBeforeReconnect);
                var exit = em.EntityQuery<TarkovExitComponent>().Last().Owner;
                var at = em.GetComponent<TransformComponent>(exit).Coordinates;
                em.System<SharedTransformSystem>().SetCoordinates(bodyBeforeReconnect,
                    new Robust.Shared.Map.EntityCoordinates(at.EntityId, at.Position + new System.Numerics.Vector2(0, 1.5f)));
            });
            await pair.RunTicksSync(12); await second.WaitRunTicks(12);
            await server.WaitAssertion(() => positionBeforeReconnect = server.EntMan.GetComponent<TransformComponent>(bodyBeforeReconnect).LocalPosition);
            await second.WaitPost(() => second.Resolve<IClientNetManager>().ClientDisconnect("Raid reconnect check"));
            await pair.RunTicksSync(30); await second.WaitRunTicks(30);
            second.SetConnectTarget(server);
            await second.WaitPost(() => second.Resolve<IClientNetManager>().ClientConnect(null!, 0, "TarkovSecond"));
            for (var i = 0; i < 3; i++) { await pair.RunTicksSync(30); await second.WaitRunTicks(30); }
            Assert.That(mode.Repository.Read().Accounts[other].Life, Is.EqualTo(lifeBeforeReconnect));
            Assert.That(mode.Repository.Read().Accounts[other].Raid, Is.EqualTo(activeRaidId));
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var pc = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == other && !p.Closed);
                Assert.That(pc.Owner, Is.EqualTo(bodyBeforeReconnect), "Reconnect must reuse the same raid body");
                Assert.That(System.Numerics.Vector2.Distance(em.GetComponent<TransformComponent>(pc.Owner).LocalPosition, positionBeforeReconnect),
                    Is.LessThan(0.2f), "Reconnect must not grant another random landing");
            });
            EntityUid combatBody = default, armedGuard = default, guardGun = default;
            var initialGuardAmmo = 0;
            var damageBeforeGuard = 0f;
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                combatBody = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                em.RemoveComponent<GodmodeComponent>(combatBody);
                var exit = em.EntityQuery<TarkovExitComponent>().First().Owner;
                var coordinates = em.GetComponent<TransformComponent>(exit).Coordinates;
                var arena = new Robust.Shared.Map.EntityCoordinates(coordinates.EntityId, coordinates.Position + new System.Numerics.Vector2(0, 2));
                em.System<SharedTransformSystem>().SetCoordinates(combatBody, arena);
                damageBeforeGuard = em.GetComponent<DamageableComponent>(combatBody).TotalDamage.Float();
                // Isolate the combat fixture from roaming inhabitants so their shots cannot satisfy this assertion.
                foreach (var npc in em.EntityQuery<Content.Server.NPC.HTN.HTNComponent>().ToArray())
                    if (em.GetComponent<TransformComponent>(npc.Owner).MapUid == em.GetComponent<TransformComponent>(combatBody).MapUid
                        && !em.HasComponent<TarkovPlayerComponent>(npc.Owner)) em.QueueDeleteEntity(npc.Owner);
                armedGuard = em.SpawnEntity("TarkovStationRaidGuard", new Robust.Shared.Map.EntityCoordinates(coordinates.EntityId,
                    arena.Position - new System.Numerics.Vector2(2.5f, 0)));
                guardGun = em.System<SharedHandsSystem>().EnumerateHeld(armedGuard).Single(i => em.HasComponent<GunComponent>(i));
                var ammunition = new GetAmmoCountEvent(); em.EventBus.RaiseLocalEvent(guardGun, ref ammunition);
                initialGuardAmmo = ammunition.Count;
                Assert.That(initialGuardAmmo, Is.GreaterThan(0));
            });
            var guardHit = false;
            for (var i = 0; i < 180 && !guardHit; i++)
            {
                await pair.RunTicksSync(1); await second.WaitRunTicks(1);
                await server.WaitAssertion(() =>
                {
                    Assert.That(server.EntMan.EntityExists(combatBody), Is.True);
                    guardHit = server.EntMan.GetComponent<DamageableComponent>(combatBody).TotalDamage.Float() > damageBeforeGuard;
                });
            }
            var remainingGuardAmmo = initialGuardAmmo;
            var guardDiagnostics = "";
            await server.WaitPost(() =>
            {
                var em = server.EntMan;
                var ammunition = new GetAmmoCountEvent(); em.EventBus.RaiseLocalEvent(guardGun, ref ammunition);
                remainingGuardAmmo = ammunition.Count;
                var htn = em.GetComponent<Content.Server.NPC.HTN.HTNComponent>(armedGuard);
                var diagnostics = $"AI enabled={em.System<Content.Server.NPC.Systems.NPCSystem>().Enabled}, awake={em.HasComponent<Content.Shared.NPC.ActiveNPCComponent>(armedGuard)}, hit={guardHit}, ammo={initialGuardAmmo}->{remainingGuardAmmo}, plan={htn.Plan?.CurrentOperator}, planning={htn.Planning}\n"
                    + "Gun=" + em.GetComponent<MetaDataComponent>(guardGun).EntityPrototype?.ID
                    + " bolt=" + em.GetComponent<ChamberMagazineAmmoProviderComponent>(guardGun).BoltClosed
                    + " status=" + em.GetComponent<Content.Server.NPC.Components.NPCRangedCombatComponent>(armedGuard).Status
                    + "\nGuard factions=" + string.Join(',', em.GetComponent<Content.Shared.NPC.Components.NpcFactionMemberComponent>(armedGuard).Factions)
                    + "\nPlayer factions=" + string.Join(',', em.GetComponent<Content.Shared.NPC.Components.NpcFactionMemberComponent>(combatBody).Factions)
                    + "\nBlackboard=" + string.Join(';', htn.Blackboard.Select(p => p.Key + "=" + p.Value));
                guardDiagnostics = diagnostics;
            });
            TestContext.Progress.WriteLine(guardDiagnostics);
            Assert.That(guardHit, Is.True, "Native HTN must make the armed scavenger attack a nearby player");
            Assert.That(remainingGuardAmmo, Is.LessThan(initialGuardAmmo), "The scavenger must fire its weapon rather than just hold it");
            // Exhaust the carried weapon and verify actual HTN reload with finite existing magazines.
            await server.WaitPost(() =>
            {
                var em = server.EntMan;
                em.EnsureComponent<GodmodeComponent>(combatBody);
                var drain = new TakeAmmoEvent(1000, new(), em.GetComponent<TransformComponent>(guardGun).Coordinates, null);
                em.EventBus.RaiseLocalEvent(guardGun, drain);
                foreach (var (ammo, _) in drain.Ammo) if (ammo != null) em.QueueDeleteEntity(ammo.Value);
            });
            var reloaded = false;
            for (var i = 0; i < 360 && !reloaded; i++)
            {
                await pair.RunTicksSync(1); await second.WaitRunTicks(1);
                await server.WaitAssertion(() => reloaded = server.EntMan.GetComponent<TarkovRaiderComponent>(armedGuard).Reloads > 0);
            }
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                Assert.That(em.GetComponent<TarkovRaiderComponent>(armedGuard).Reloads, Is.GreaterThan(0),
                    "Native HTN must reload after the magazine is exhausted");
                Assert.That(em.System<SharedGunSystem>().GetMagazineEntity(guardGun), Is.Not.Null);
                var bagEntity = em.System<InventorySystem>().GetHandOrInventoryEntities((armedGuard, null, null))
                    .Single(item => em.HasComponent<StorageComponent>(item));
                foreach (var item in em.GetComponent<StorageComponent>(bagEntity).Container.ContainedEntities.ToArray())
                    if (em.HasComponent<Content.Shared.Weapons.Ranged.Components.BallisticAmmoProviderComponent>(item)) em.QueueDeleteEntity(item);
                var drain = new TakeAmmoEvent(1000, new(), em.GetComponent<TransformComponent>(guardGun).Coordinates, null);
                em.EventBus.RaiseLocalEvent(guardGun, drain);
                foreach (var (ammo, _) in drain.Ammo) if (ammo != null) em.QueueDeleteEntity(ammo.Value);
            });
            var knifeEquipped = false;
            for (var i = 0; i < 180 && !knifeEquipped; i++)
            {
                await pair.RunTicksSync(1); await second.WaitRunTicks(1);
                await server.WaitAssertion(() => knifeEquipped = server.EntMan.GetComponent<TarkovRaiderComponent>(armedGuard).KnifeMode);
            }
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                Assert.That(em.GetComponent<TarkovRaiderComponent>(armedGuard).KnifeMode, Is.True);
                var held = em.System<SharedHandsSystem>().GetActiveItem(armedGuard);
                Assert.That(held, Is.Not.Null);
                Assert.That(em.GetComponent<MetaDataComponent>(held!.Value).EntityPrototype?.ID, Is.EqualTo("CombatKnife"));
                Assert.That(em.EntityExists(guardGun), Is.True, "The empty weapon must remain real loot");
                em.RemoveComponent<GodmodeComponent>(combatBody);
                damageBeforeGuard = em.GetComponent<DamageableComponent>(combatBody).TotalDamage.Float();
            });
            var knifeHit = false;
            for (var i = 0; i < 360 && !knifeHit; i++)
            {
                await pair.RunTicksSync(1); await second.WaitRunTicks(1);
                await server.WaitAssertion(() => knifeHit = server.EntMan.GetComponent<DamageableComponent>(combatBody).TotalDamage.Float() > damageBeforeGuard);
            }
            Assert.That(knifeHit, Is.True, "An empty guard must pursue and attack with the carried knife");
            await server.WaitPost(() => server.EntMan.QueueDeleteEntity(armedGuard));
            EntityUid looter = default, lootItem = default;
            var looterStart = System.Numerics.Vector2.Zero;
            await server.WaitPost(() =>
            {
                var em = server.EntMan;
                em.EnsureComponent<GodmodeComponent>(combatBody);
                var at = em.GetComponent<TransformComponent>(combatBody).Coordinates;
                looterStart = at.Position + new System.Numerics.Vector2(-5, 0);
                looter = em.SpawnEntity("TarkovStationRaidGuard", new Robust.Shared.Map.EntityCoordinates(at.EntityId, looterStart));
                var factions = em.System<Content.Shared.NPC.Systems.NpcFactionSystem>();
                factions.ClearFactions(looter);
                foreach (var faction in em.GetComponent<Content.Shared.NPC.Components.NpcFactionMemberComponent>(combatBody).Factions)
                    factions.AddFaction(looter, faction.Id);
                var cache = em.SpawnEntity("CrateGenericSteel", new Robust.Shared.Map.EntityCoordinates(at.EntityId, at.Position + new System.Numerics.Vector2(-1, 0)));
                var cacheTag = em.EnsureComponent<TarkovItemComponent>(cache);
                cacheTag.Id = Guid.NewGuid().ToString("N"); cacheTag.FoundRaid = activeRaidId;
                lootItem = em.SpawnEntity("Wrench", em.GetComponent<TransformComponent>(cache).Coordinates);
                var itemTag = em.EnsureComponent<TarkovItemComponent>(lootItem);
                itemTag.Id = Guid.NewGuid().ToString("N"); itemTag.FoundRaid = activeRaidId;
                em.System<Content.Shared.Storage.EntitySystems.SharedEntityStorageSystem>().Insert(lootItem, cache);
            });
            var collected = false;
            for (var i = 0; i < 600 && !collected; i++)
            {
                await pair.RunTicksSync(1); await second.WaitRunTicks(1);
                await server.WaitAssertion(() => collected = server.EntMan.GetComponent<TarkovRaiderComponent>(looter).LootedItems > 0);
            }
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                Assert.That(em.GetComponent<TarkovRaiderComponent>(looter).LootedItems, Is.GreaterThan(0),
                    "An idle inhabitant must move to real loot and put items in its native backpack");
                Assert.That(System.Numerics.Vector2.Distance(em.GetComponent<TransformComponent>(looter).LocalPosition, looterStart),
                    Is.GreaterThan(0.5f), "Loot collection must include native movement");
                var bags = em.System<InventorySystem>().GetHandOrInventoryEntities((looter, null, null))
                    .Where(item => em.HasComponent<StorageComponent>(item));
                Assert.That(bags.SelectMany(item => em.GetComponent<StorageComponent>(item).Container.ContainedEntities)
                    .Any(item => em.TryGetComponent<TarkovItemComponent>(item, out var tag) && tag.FoundRaid == activeRaidId), Is.True,
                    "Collected objects retain their raid provenance and can be looted from the inhabitant");
                em.RemoveComponent<GodmodeComponent>(combatBody);
                em.QueueDeleteEntity(looter);
            });

            // Exercise both chambered long-gun archetypes, not just the pistol case.
            foreach (var archetype in new[] { "TarkovStationRaidGuardPatrol", "TarkovStationRaidGuardVeteran" })
            {
                EntityUid variant = default, variantGun = default;
                var ammoBefore = 0;
                var piercingBefore = 0f;
                await server.WaitPost(() =>
                {
                    var em = server.EntMan;
                    var at = em.GetComponent<TransformComponent>(combatBody).Coordinates;
                    variant = em.SpawnEntity(archetype, new Robust.Shared.Map.EntityCoordinates(at.EntityId,
                        at.Position - new System.Numerics.Vector2(2.5f, 0)));
                    variantGun = em.System<SharedHandsSystem>().EnumerateHeld(variant).Single(item => em.HasComponent<GunComponent>(item));
                    Assert.That(em.GetComponent<MetaDataComponent>(variantGun).EntityPrototype?.ID,
                        Is.EqualTo(archetype.EndsWith("Patrol") ? "WeaponSubMachineGunDrozd" : "WeaponRifleAk"));
                    var ammo = new GetAmmoCountEvent(); em.EventBus.RaiseLocalEvent(variantGun, ref ammo); ammoBefore = ammo.Count;
                    piercingBefore = em.GetComponent<DamageableComponent>(combatBody).Damage.DamageDict.GetValueOrDefault("Piercing").Float();
                });
                var firedAndHit = false;
                for (var i = 0; i < 360 && !firedAndHit; i++)
                {
                    await pair.RunTicksSync(1); await second.WaitRunTicks(1);
                    await server.WaitAssertion(() =>
                    {
                        var em = server.EntMan;
                        var ammo = new GetAmmoCountEvent(); em.EventBus.RaiseLocalEvent(variantGun, ref ammo);
                        firedAndHit = ammo.Count < ammoBefore && em.GetComponent<DamageableComponent>(combatBody)
                            .Damage.DamageDict.GetValueOrDefault("Piercing").Float() > piercingBefore;
                    });
                }
                Assert.That(firedAndHit, Is.True, archetype + " must chamber ammunition and hit using native combat");
                await server.WaitPost(() => server.EntMan.QueueDeleteEntity(variant));
            }
            if (mode.Repository.Read().Accounts[user].Location == "raid") await Request(TarkovAction.Extract);
            await pair.RunTicksSync(60); await second.WaitRunTicks(60);
            await server.WaitPost(() =>
            {
                var em = server.EntMan;
                var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == other && !p.Closed).Owner;
                var exit = em.EntityQuery<TarkovExitComponent>().First().Owner;
                em.System<SharedTransformSystem>().SetCoordinates(body, em.GetComponent<TransformComponent>(exit).Coordinates);
            });
            await SecondRequest(TarkovAction.Extract);
            await pair.RunTicksSync(90); await second.WaitRunTicks(90);
            Assert.That(mode.Repository.Read().Accounts[other].Location, Is.EqualTo("hub"));
            await server.WaitAssertion(() => Assert.That(server.EntMan.EntityQuery<TarkovRaidComponent>(), Is.Empty,
                "An empty raid must end before its 15-minute deadline"));
            await Request(TarkovAction.LeaveParty);
            await SecondRequest(TarkovAction.LeaveParty);
            foreach (var expectedPhase in new[] { TarkovDayPhase.Night, TarkovDayPhase.Day, TarkovDayPhase.Evening })
            {
                await server.WaitPost(() =>
                {
                    var pc = server.EntMan.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed);
                    server.EntMan.EnsureComponent<GodmodeComponent>(pc.Owner);
                });
                await Request(TarkovAction.Ready);
                for (var i = 0; i < 100 && mode.Repository.Read().Accounts[user].Location != "raid"; i++)
                { await pair.RunTicksSync(30); await second.WaitRunTicks(30); }
                Assert.That(mode.Repository.Read().Accounts[user].Location, Is.EqualTo("raid"));
                await server.WaitAssertion(() =>
                {
                    var em = server.EntMan;
                    var raid = em.EntityQuery<TarkovRaidComponent>().Single();
                    Assert.That(raid.DayPhase, Is.EqualTo(expectedPhase));
                    Assert.That(em.GetComponent<Robust.Shared.Map.Components.MapLightComponent>(raid.Owner).AmbientLightColor,
                        Is.EqualTo(TarkovRaidConditions.Ambient(expectedPhase)), "Native planetary visibility must match the chosen phase");
                    Assert.That((raid.EndsAt - server.Resolve<Robust.Shared.Timing.IGameTiming>().CurTime).TotalSeconds,
                        Is.InRange(890, 900), "Every newly created raid gets exactly fifteen minutes");
                    if (expectedPhase == TarkovDayPhase.Night)
                        raid.EndsAt = server.Resolve<Robust.Shared.Timing.IGameTiming>().CurTime + TimeSpan.FromMilliseconds(100);
                    else if (expectedPhase == TarkovDayPhase.Evening)
                    {
                        var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                        em.QueueDeleteEntity(body);
                    }
                    else
                    {
                        var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                        var exit = em.EntityQuery<TarkovExitComponent>().First().Owner;
                        em.System<SharedTransformSystem>().SetCoordinates(body, em.GetComponent<TransformComponent>(exit).Coordinates);
                    }
                });
                if (expectedPhase == TarkovDayPhase.Day) await Request(TarkovAction.Extract);
                await pair.RunTicksSync(90); await second.WaitRunTicks(90);
                Assert.That(mode.Repository.Read().Accounts[user].Location, Is.EqualTo("hub"));
                await server.WaitAssertion(() => Assert.That(server.EntMan.EntityQuery<TarkovRaidComponent>(), Is.Empty));
            }
            await second.WaitPost(() => second.Resolve<IClientNetManager>().ClientDisconnect("Completed"));
            await pair.RunTicksSync(30); await second.WaitRunTicks(30);
            await server.WaitAssertion(() => Assert.That(mode.Admin(new[] { "cycle", "1" }), Is.EqualTo("tarkov-success")));
            // Cycle is deliberately UTC-based. Wait for actual expiry while continuing both simulation loops.
            await Task.Delay(1100);
            await pair.RunTicksSync(60);
            Assert.That(mode.Repository.Read().Accounts, Is.Empty);
            Assert.That(mode.Repository.Read().Results.Count, Is.EqualTo(2));
            await server.WaitPost(() => server.Resolve<ILogManager>().RootSawmill.RemoveHandler(runtimeErrors));
            await client.WaitPost(() => client.Resolve<ILogManager>().RootSawmill.RemoveHandler(runtimeErrors));
            Assert.That(runtimeErrors.Errors, Is.Empty, "A successful gameplay assertion must not hide engine runtime exceptions");
            await pair.CleanReturnAsync();
        }
        catch (Exception exception)
        {
            // The pool can report additional cleanup warnings; keep the primary scenario failure visible first.
            Console.Error.WriteLine("Tarkov native scenario primary failure: " + exception);
            throw;
        }
    }
    private sealed class RuntimeErrors : ILogHandler
    {
        public readonly ConcurrentQueue<string> Errors = new();
        public void Log(string sawmillName, LogEvent message)
        {
            if (sawmillName == "runtime" && message.Level >= LogEventLevel.Error)
                Errors.Enqueue(message.RenderMessage() + "\n" + message.Exception);
        }
    }

}
