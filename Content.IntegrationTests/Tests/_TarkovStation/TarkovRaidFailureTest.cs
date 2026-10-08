// SPDX-License-Identifier: AGPL-3.0-or-later
#nullable enable
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Client._TarkovStation;
using Content.Server._TarkovStation;
using Content.Server.GameTicking;
using Content.Server.Ghost;
using Content.Shared._TarkovStation;
using Content.Shared._TarkovStation.Components;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Actions.Events;
using Content.Shared.CCVar;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Interaction;
using Content.Shared.Light.Components;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.IntegrationTests.Tests._TarkovStation;

[TestFixture]
public sealed class TarkovRaidFailureTest
{
    [Test]
    public async Task NativeDamageSuccumbAndClientDaylight()
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
            server.CfgMan.SetCVar(TarkovCVars.TestBots, false);
            server.CfgMan.SetCVar(CCVars.GameLobbyFallbackEnabled, false);
            var ticker = server.System<GameTicker>();
            ticker.SetGamePreset("TarkovStation");
            ticker.StartRound(true);
        });
        await pair.RunTicksSync(60);
        async Task Request(TarkovAction action, string id = "", string extra = "")
        {
            if (action == TarkovAction.Ready)
            {
                await server.WaitPost(() =>
                {
                    var em = server.EntMan;
                    var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                    var console = em.EntityQuery<TarkovTerminalComponent>().First(t => t.Page == "raids").Owner;
                    var at = em.GetComponent<TransformComponent>(console).Coordinates;
                    em.System<SharedTransformSystem>().SetCoordinates(body, new EntityCoordinates(at.EntityId, at.Position + Vector2.UnitY));
                    var activation = new InteractHandEvent(body, console);
                    em.EventBus.RaiseLocalEvent(console, activation);
                    Assert.That(activation.Handled, Is.True);
                });
            }
            await client.WaitPost(() => client.System<TarkovClientSystem>().Send(new TarkovRequestEvent
            {
                Action = action, Id = id, Extra = extra, AcceptedRules = true,
                Profile = action == TarkovAction.Create ? HumanoidCharacterProfile.DefaultWithSpecies("Human").WithName("Рейд Проверка") : null,
            }));
            await pair.RunTicksSync(15);
            await client.WaitAssertion(() => Assert.That(client.System<TarkovClientSystem>().LastActionMessage,
                Does.Not.StartWith("tarkov-error"), action.ToString()));
        }
        await Request(TarkovAction.AcceptRules);
        await Request(TarkovAction.Create, "Scavengers", "Assault");
        foreach (var phase in new[] { TarkovDayPhase.Day, TarkovDayPhase.Evening, TarkovDayPhase.Night })
        {
            await Request(TarkovAction.Ready);
            for (var i = 0; i < 100 && mode.Repository!.Read().Accounts[user].Location != "raid"; i++)
                await pair.RunTicksSync(30);
            Assert.That(mode.Repository!.Read().Accounts[user].Location, Is.EqualTo("raid"));
            await pair.RunTicksSync(30);
            await client.WaitAssertion(() =>
            {
                var em = client.EntMan;
                var body = client.Resolve<Robust.Client.Player.IPlayerManager>().LocalEntity!.Value;
                var map = em.GetComponent<TransformComponent>(body).MapUid!.Value;
                Assert.That(em.GetComponent<MapLightComponent>(map).AmbientLightColor,
                    Is.EqualTo(TarkovRaidConditions.Ambient(phase)), "The renderer must receive the advertised daylight, not just the server map");
                Assert.That(!em.TryGetComponent<LightCycleComponent>(map, out var cycle) || !cycle.Enabled, Is.True,
                    "The client must not apply a random nighttime offset to the fixed raid phase");
                Assert.That(em.HasComponent<SunShadowCycleComponent>(map), Is.False,
                    "Sun shadows must use the raid phase, not an independent randomized clock");
            });
            var before = mode.Repository.Read().Accounts[user];
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                var mind = em.GetComponent<MindContainerComponent>(body).Mind!.Value;
                Assert.That(em.System<GhostSystem>().OnGhostAttempt(mind, true, viaCommand: true), Is.False,
                    "A healthy player cannot use ghost as a free evacuation");
                Assert.That(em.GetComponent<TarkovPlayerComponent>(body).Closed, Is.False);
                if (phase == TarkovDayPhase.Day)
                {
                    // Real damage goes through wounds, vital thresholds and MobState events.
                    em.System<DamageableSystem>().ChangeDamage(body, new DamageSpecifier { DamageDict = { ["Piercing"] = 300 } },
                        ignoreResistances: true, targetPart: TargetBodyPart.Chest);
                    Assert.That(em.GetComponent<TarkovPlayerComponent>(body).Closed, Is.True,
                        "Lethal vital damage must close the raid life without forcing MobState.Dead in the test");
                }
                else
                {
                    var state = phase == TarkovDayPhase.Evening ? MobState.SoftCritical : MobState.HardCritical;
                    em.System<DamageableSystem>().ChangeDamage(body,
                        new DamageSpecifier { DamageDict = { ["Piercing"] = phase == TarkovDayPhase.Evening ? 130 : 170 } },
                        ignoreResistances: true, targetPart: TargetBodyPart.Chest);
                    Assert.That(em.GetComponent<MobStateComponent>(body).CurrentState, Is.EqualTo(state));
                    if (phase == TarkovDayPhase.Evening)
                        em.EventBus.RaiseLocalEvent(body, new CritSuccumbEvent { Performer = body });
                    else
                        em.EventBus.RaiseLocalEvent(body, new CritRageQuitEvent { Performer = body });
                    Assert.That(em.GetComponent<TarkovPlayerComponent>(body).Closed, Is.True,
                        "Succumb/ghost in either critical state must end the raid life while preventing observer scouting");
                }
            });
            await pair.RunTicksSync(90);
            var after = mode.Repository.Read().Accounts[user];
            Assert.That(after.Location, Is.EqualTo("hub"));
            Assert.That(after.Life, Is.Not.EqualTo(before.Life));
            Assert.That(after.Balance, Is.EqualTo(before.Balance));
            await server.WaitAssertion(() =>
            {
                var em = server.EntMan;
                var body = em.EntityQuery<TarkovPlayerComponent>().Single(p => p.User == user && !p.Closed).Owner;
                Assert.That(em.GetComponent<MobStateComponent>(body).CurrentState, Is.EqualTo(MobState.Alive));
                Assert.That(em.GetComponent<TransformComponent>(body).MapUid, Is.EqualTo(mode.Hub));
                Assert.That(em.EntityQuery<TarkovRaidComponent>(), Is.Empty, "The last dead participant must release the active raid");
            });
        }
        await pair.CleanReturnAsync();
    }
}
