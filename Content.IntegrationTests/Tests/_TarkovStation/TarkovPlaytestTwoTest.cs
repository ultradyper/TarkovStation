// SPDX-License-Identifier: AGPL-3.0-or-later
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using Content.Client._TarkovStation;
using Content.Server._TarkovStation;
using Content.Server._TarkovStation.Persistence;
using Content.Server.GameTicking;
using Content.Server.RoundEnd;
using Content.Shared._TarkovStation;
using Content.Shared._TarkovStation.Components;
using Content.Shared._TarkovStation.Prototypes;
using Content.Shared.CCVar;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Damage.Components;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Preferences;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._TarkovStation;

[TestFixture]
public sealed class TarkovPlaytestTwoTest
{
    [Test]
    public void DestinationPersistsAndContainsAllMapSizesWithoutPopulationInput()
    {
        var plans = Enumerable.Range(0, 100).Select(n => TarkovRaidPlanner.Create("test-cycle", n)).ToArray();
        Assert.That(plans.Select(p => p.Radius).Distinct(), Is.EquivalentTo(new[] { 40, 60, 80 }));
        Assert.That(plans.Select(p => p.Event).Distinct().Count(), Is.EqualTo(2));
        for (var n = 0; n < plans.Length; n++)
            Assert.That(TarkovRaidPlanner.Create("test-cycle", n), Is.EqualTo(plans[n]));
    }

    [TestCase(TarkovRaidEventKind.Airdrop)]
    [TestCase(TarkovRaidEventKind.Migration)]
    public async Task NativeOnboardingMarketEventAndDeathLock(TarkovRaidEventKind eventKind)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            InLobby = true, Map = "TarkovStationHub", Fresh = true, Destructive = true,
        });
        var server = pair.Server;
        var client = pair.Client;
        var mode = server.System<TarkovSystem>();
        var user = client.User!.Value.ToString();
        var timing = server.Resolve<IGameTiming>();
        EntityUid Player() => server.EntMan.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
        TarkovStateEvent Snapshot() => (TarkovStateEvent) typeof(TarkovClientSystem)
            .GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client.System<TarkovClientSystem>())!;
        await server.WaitPost(() =>
        {
            server.CfgMan.SetCVar(TarkovCVars.Enabled, true);
            server.CfgMan.SetCVar(TarkovCVars.QueueSeconds, 3);
            server.CfgMan.SetCVar(TarkovCVars.TestBots, false);
            server.CfgMan.SetCVar(Content.Goobstation.Common.CCVar.GoobCVars.DisablePathfinding, false);
            server.CfgMan.SetCVar(CCVars.GameLobbyFallbackEnabled, false);
            var ticker = server.System<GameTicker>();
            ticker.SetGamePreset("TarkovStation");
            ticker.StartRound(true);
        });
        await pair.RunTicksSync(60);
        async Task Request(TarkovAction action, string id = "", string extra = "", string? error = null)
        {
            if (TarkovServiceAccess.PageFor(action) is { Length: > 0 } page)
            {
                await server.WaitPost(() =>
                {
                    var em = server.EntMan;
                    var body = Player();
                    var terminal = em.EntityQuery<TarkovTerminalComponent>().First(t => t.Page == page).Owner;
                    var at = em.GetComponent<TransformComponent>(terminal).Coordinates;
                    em.System<SharedTransformSystem>().SetCoordinates(body,
                        new EntityCoordinates(at.EntityId, at.Position + new Vector2(0, at.Position.Y < 0 ? 1 : -1)));
                    var interaction = new InteractHandEvent(body, terminal);
                    em.EventBus.RaiseLocalEvent(terminal, interaction);
                    Assert.That(interaction.Handled, Is.True);
                });
            }
            await client.WaitPost(() =>
            {
                var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithName("Полевой Испытатель");
                profile = profile.WithCharacterAppearance(profile.Appearance.WithMarkings(new List<Marking> { new("ForbiddenTail", 1) }));
                client.System<TarkovClientSystem>().Send(new TarkovRequestEvent
                {
                    Action = action, Id = id, Extra = extra, AcceptedRules = true,
                    Profile = action == TarkovAction.Create ? profile : null,
                });
            });
            await pair.RunTicksSync(15);
            await client.WaitAssertion(() =>
            {
                var actual = client.System<TarkovClientSystem>().LastActionMessage;
                if (error != null) Assert.That(actual, Is.EqualTo(error), action.ToString());
                else Assert.That(actual, Does.Not.StartWith("tarkov-error"), action.ToString());
            });
        }
        await Request(TarkovAction.AcceptGuide, error: "tarkov-error-rules");
        await Request(TarkovAction.AcceptRules);
        await Request(TarkovAction.Create, "Scavengers", "Assault", "tarkov-error-guide");
        Assert.That(mode.Repository!.Read().Accounts.ContainsKey(user), Is.False);
        await Request(TarkovAction.AcceptGuide);
        await Request(TarkovAction.Create, "Scavengers", "Assault");
        Assert.That(mode.Repository.Read().Accounts[user].Profile, Does.Not.Contain("ForbiddenTail"));
        await server.WaitAssertion(() =>
        {
            var em = server.EntMan;
            Assert.That(em.GetComponent<HungerComponent>(Player()).ActualDecayRate, Is.InRange(0.001f, 0.0151f));
            Assert.That(em.GetComponent<ThirstComponent>(Player()).ActualDecayRate, Is.InRange(0.001f, 0.0451f));
            server.CfgMan.SetCVar(CCVars.EmergencyShuttleAutoCallTime, 1);
            em.System<RoundEndSystem>().AutoCallStartTime = timing.CurTime - TimeSpan.FromMinutes(2);
            mode.Repository.Execute(user, "qa-budget", "test", d => { d.Accounts[user].Balance = 50000; return null; }, 100);
        });
        await pair.RunTicksSync(10);
        await server.WaitAssertion(() => Assert.That(server.System<RoundEndSystem>().ExpectedCountdownEnd, Is.Null));
        await Request(TarkovAction.Buy, "TarkovStationSurgicalKit");
        await Request(TarkovAction.Buy, "EmergencyMedipen");
        if (eventKind == TarkovRaidEventKind.Airdrop)
        {
            // Native quote -> stash -> sale, including emptied medical consumables.
            var junkId = Guid.NewGuid().ToString("N");
            await server.WaitPost(() =>
            {
                var em = server.EntMan;
                var hands = em.System<SharedHandsSystem>();
                hands.TryDrop(Player());
                var paper = em.SpawnEntity("Paper", em.GetComponent<TransformComponent>(Player()).Coordinates);
                var tag = em.AddComponent<TarkovItemComponent>(paper);
                tag.Id = junkId;
                tag.FoundRaid = "fixture-recovered-loot";
                Assert.That(hands.TryPickup(Player(), paper), Is.True);
            });
            await Request(TarkovAction.Deposit, junkId);
            Assert.That(mode.Repository.Read().Items[junkId].Value, Is.EqualTo(5));
            var cash = mode.Repository.Read().Accounts[user].Balance;
            await Request(TarkovAction.Sell, junkId);
            Assert.That(mode.Repository.Read().Accounts[user].Balance, Is.EqualTo(cash + 5));
            var penId = mode.Repository.Read().Items.Values.Single(i => i.Owner == user && i.Prototype == "EmergencyMedipen" && i.Parent == "").Id;
            await Request(TarkovAction.Withdraw, penId);
            await server.WaitPost(() =>
            {
                var em = server.EntMan;
                var pen = em.EntityQuery<TarkovItemComponent>().Single(i => i.Id == penId).Owner;
                var solutions = em.System<SharedSolutionContainerSystem>();
                Assert.That(solutions.TryGetSolution(pen, "pen", out var solution, out _), Is.True);
                solutions.RemoveAllSolution(solution!.Value);
            });
            await Request(TarkovAction.Deposit, penId);
            Assert.That(mode.Repository.Read().Items[penId].Value, Is.EqualTo(2), "A spent pen cannot retain the full medicine quote");
            var casingId = Guid.NewGuid().ToString("N");
            await server.WaitPost(() =>
            {
                var em = server.EntMan;
                var round = em.SpawnEntity("CartridgePistol", em.GetComponent<TransformComponent>(Player()).Coordinates);
                var tag = em.AddComponent<TarkovItemComponent>(round);
                tag.Id = casingId; tag.FoundRaid = "fixture-recovered-loot";
                Assert.That(em.System<SharedHandsSystem>().TryPickup(Player(), round), Is.True);
            });
            await Request(TarkovAction.Deposit, casingId);
            Assert.That(mode.Repository.Read().Items[casingId].Value, Is.Zero, "Loose rounds do not bypass emergency ammunition provenance through scrap valuation");
        }
        await server.WaitAssertion(() =>
        {
            var data = mode.Repository.Read();
            var kit = data.Items.Values.Single(i => i.Owner == user && i.Prototype == "TarkovStationSurgicalKit" && i.Parent == "");
            foreach (var tool in new[] { "Scalpel", "Hemostat", "Retractor", "Cautery", "Saw", "Drill", "Bonesetter", "BoneGel" })
                Assert.That(data.Items.Values.Any(i => i.Parent == kit.Id && i.Prototype == tool), Is.True, tool);
            Assert.That(kit.Value, Is.LessThan(1900));
            Assert.That(server.ProtoMan.EnumeratePrototypes<TarkovGoodsPrototype>().Count(), Is.GreaterThanOrEqualTo(90));
            // Spawn every product, catching invalid composed native prototypes and StorageFill errors.
            foreach (var goods in server.ProtoMan.EnumeratePrototypes<TarkovGoodsPrototype>())
            {
                var item = server.EntMan.SpawnEntity(goods.Product, new EntityCoordinates(mode.Hub!.Value, new Vector2(3, 3)));
                server.EntMan.QueueDeleteEntity(item);
            }
            mode.Repository.Execute(user, "qa-destination", "test", d =>
            {
                while (TarkovRaidPlanner.Create(d.Cycle, d.RaidSequence) is var plan && (plan.Radius != 80 || plan.Event != eventKind)) d.RaidSequence++;
                return null;
            }, 100);
        });
        await Request(TarkovAction.Refresh);
        TarkovStateEvent? preview = null;
        await client.WaitAssertion(() => { preview = Snapshot(); Assert.That(preview.RaidRadius, Is.EqualTo(80)); Assert.That(preview.RaidEvent, Is.EqualTo(eventKind)); });
        await Request(TarkovAction.Ready);
        for (var i = 0; i < 120 && mode.Repository.Read().Accounts[user].Location != "raid"; i++) await pair.RunTicksSync(30);
        Assert.That(mode.Repository.Read().Accounts[user].Location, Is.EqualTo("raid"));
        TarkovRaidComponent raid = null!;
        EntityUid raidMap = default;
        var otherUser = Guid.NewGuid().ToString();
        await server.WaitPost(() =>
        {
            var em = server.EntMan;
            raid = em.EntityQuery<TarkovRaidComponent>().Single();
            raidMap = raid.Owner;
            Assert.That(raid.Radius, Is.EqualTo(preview!.RaidRadius), "A solo player must actually get the advertised large destination");
            Assert.That(raid.Biome, Is.EqualTo(preview.RaidBiome));
            Assert.That(raid.EventKind, Is.EqualTo(preview.RaidEvent));
            em.EnsureComponent<GodmodeComponent>(Player());
            // Keep another participant alive so the empty-raid rule cannot hide a missing death lock.
            var survivor = em.SpawnEntity("MobHuman", em.GetComponent<TransformComponent>(Player()).Coordinates);
            var pc = em.AddComponent<TarkovPlayerComponent>(survivor);
            pc.User = otherUser; pc.Raid = raid.Id;
            em.EnsureComponent<GodmodeComponent>(survivor);
            raid.Participants.Add(otherUser);
            mode.Repository.Execute(user, "qa-survivor", "test", d =>
            {
                d.Accounts[otherUser] = new TarkovAccount { User = otherUser, Created = true, TestBot = true, Location = "raid", Raid = raid.Id };
                return null;
            }, 100);
            raid.EventAt = timing.CurTime + TimeSpan.FromSeconds(2);
        });
        await pair.RunTicksSync(90);
        await client.WaitAssertion(() => Assert.That(Snapshot().EventStage, Is.GreaterThanOrEqualTo(1)));
        var originalPositions = new Dictionary<EntityUid, Vector2>();
        await pair.RunTicksSync(420);
        await server.WaitAssertion(() =>
        {
            var em = server.EntMan;
            if (eventKind == TarkovRaidEventKind.Airdrop)
            {
                Assert.That(raid.EventStage, Is.EqualTo(3));
                var crate = em.EntityQuery<TarkovItemComponent>().Single(i => em.GetComponent<MetaDataComponent>(i.Owner).EntityPrototype?.ID == "TarkovStationAirdropCrate");
                Assert.That(crate.FoundRaid, Is.EqualTo(raid.Id));
                Assert.That(em.GetComponent<EntityStorageComponent>(crate.Owner).Contents.ContainedEntities, Is.Not.Empty);
            }
            else
            {
                var migrants = em.EntityQuery<TarkovMigrationComponent>().ToArray();
                Assert.That(migrants.Length, Is.GreaterThanOrEqualTo(3));
                foreach (var migrant in migrants) originalPositions[migrant.Owner] = em.System<SharedTransformSystem>().GetWorldPosition(migrant.Owner);
            }
        });
        if (eventKind == TarkovRaidEventKind.Migration)
        {
            await pair.RunTicksSync(600);
            await server.WaitAssertion(() => Assert.That(originalPositions.Any(p => !server.EntMan.Deleted(p.Key)
                && Vector2.Distance(p.Value, server.EntMan.System<SharedTransformSystem>().GetWorldPosition(p.Key)) > 2), Is.True,
                "Fauna must actually move using native pathfinding, not merely spawn with a migration label"));
        }
        var originalLife = mode.Repository.Read().Accounts[user].Life;
        await server.WaitPost(() => server.System<MobStateSystem>().ChangeMobState(Player(), MobState.Dead));
        await pair.RunTicksSync(90);
        Assert.That(mode.Repository.Read().Accounts[user].Location, Is.EqualTo("hub"));
        Assert.That(mode.Repository.Read().Accounts[user].Life, Is.Not.EqualTo(originalLife));
        await Request(TarkovAction.Ready, error: "tarkov-error-raid-death-lock");
        await client.WaitAssertion(() => Assert.That(Snapshot().RaidReentryBlocked, Is.True));
        await server.WaitPost(() => raid.EndsAt = timing.CurTime + TimeSpan.FromMilliseconds(100));
        await pair.RunTicksSync(60);
        await Request(TarkovAction.Ready);
        await client.WaitAssertion(() => Assert.That(Snapshot().RaidReentryBlocked, Is.False));
        await Request(TarkovAction.CancelReady);
        await pair.CleanReturnAsync();
    }
}
