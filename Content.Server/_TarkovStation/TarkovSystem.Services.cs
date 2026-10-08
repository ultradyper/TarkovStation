// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Server._TarkovStation.Persistence;
using Content.Shared._TarkovStation;
using Content.Shared._TarkovStation.Components;
using Content.Shared.Damage.Components;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.CombatMode;
using Content.Shared.Preferences;
using Content.Shared.Humanoid;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._TarkovStation;

public sealed partial class TarkovSystem
{
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedCombatModeSystem _combatMode = default!;
    private void InitializeHubPolicy()
    {
        SubscribeLocalEvent<TarkovHubProtectedComponent, InteractionAttemptEvent>(OnServiceInteractionAttempt);
        SubscribeLocalEvent<TarkovHubProtectedComponent, AttackAttemptEvent>(OnServiceAttackAttempt);
        SubscribeLocalEvent<TarkovTerminalComponent, InteractHandEvent>(OnTerminal);
        SubscribeLocalEvent<TarkovTerminalComponent, InteractUsingEvent>(OnTerminalUsing);
        SubscribeLocalEvent<TarkovTerminalComponent, ActivateInWorldEvent>(OnTerminalActivate);
        SubscribeLocalEvent<Content.Server.Ghost.GhostAttemptHandleEvent>(OnGhostAttempt);
    }

    private void OnGhostAttempt(Content.Server.Ghost.GhostAttemptHandleEvent args)
    {
        if (!Enabled) return;
        // No observer scouting, including before character creation or after a raid death.
        args.Handled = true;
        args.Result = false;
    }

    private void ProtectHub()
    {
        if (_hub == null) return;
        var entities = AllEntityQuery<TransformComponent>();
        while (entities.MoveNext(out var uid, out var transform))
            if (transform.MapUid == _hub)
            {
                EnsureComp<TarkovHubProtectedComponent>(uid);
                if (HasComp<TarkovPlayerComponent>(uid)) _combatMode.SetInCombatMode(uid, false);
                if (transform.Anchored) RemComp<Content.Server.Construction.Components.ConstructionComponent>(uid);
            }
    }

    private void SetupServices()
    {
        if (_hub == null) return;
        var query = AllEntityQuery<TarkovTerminalComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var transform))
            if (transform.MapUid == _hub) return;
        var grid = HubCoordinates().EntityId;
        foreach (var (prototype, position, page) in new[]
        {
            ("TarkovStationStashTerminal", new Vector2(-4.5f, 1.5f), "stash"),
            ("TarkovStationRaidConsole", new Vector2(3.5f, -1.5f), "raids"),
            ("TarkovStationContractBoard", new Vector2(4.5f, 1.5f), "contracts"),
            ("TarkovStationResultsTerminal", new Vector2(1.5f, 1.5f), "leaderboard"),
            ("TarkovStationTradeTerminal", new Vector2(-2.5f, 1.5f), "trade"),
        })
        {
            var uid = Spawn(prototype, new EntityCoordinates(grid, position));
            EnsureComp<TarkovTerminalComponent>(uid).Page = page;
        }
        var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithName(Loc.GetString("tarkov-trader-name")).WithAge(54);
        profile = profile.WithCharacterAppearance(profile.Appearance.WithHairStyleName("HumanHairBedheadv2")
            .WithHairColor(Color.FromHex("#686052")).WithFacialHairStyleName("HumanFacialHairCroppedfullbeard")
            .WithFacialHairColor(Color.FromHex("#786D5E")).WithEyeColor(Color.FromHex("#868C70"))
            .WithSkinColor(Color.FromHex("#B99880")));
        var trader = _spawn.SpawnPlayerMob(new EntityCoordinates(grid, new Vector2(-7.5f, 0.5f)), null, profile, null);
        _spawn.EquipStartingGear(trader, "TarkovStationTraderGear");
        EnsureComp<TarkovTerminalComponent>(trader).Page = "shop";
        RemComp<Content.Shared.SSDIndicator.SSDIndicatorComponent>(trader);
        RemComp<Content.Shared.Strip.Components.StrippableComponent>(trader);
        EnsureComp<GodmodeComponent>(trader);
    }

    private void OnServiceInteractionAttempt(Entity<TarkovHubProtectedComponent> ent, ref InteractionAttemptEvent args)
    {
        if (args.Target is { } target && TryComp<TarkovTerminalComponent>(target, out var terminal)
            && OpenTerminal(ent.Owner, (target, terminal))) args.Cancelled = true;
    }

    private void OnServiceAttackAttempt(Entity<TarkovHubProtectedComponent> ent, ref AttackAttemptEvent args)
    {
        if (args.Target is { } target && TryComp<TarkovTerminalComponent>(target, out var terminal)
            && OpenTerminal(ent.Owner, (target, terminal))) args.Cancel();
    }

    private void OnTerminal(Entity<TarkovTerminalComponent> ent, ref InteractHandEvent args)
    {
        if (OpenTerminal(args.User, ent)) args.Handled = true;
    }

    private void OnTerminalUsing(Entity<TarkovTerminalComponent> ent, ref InteractUsingEvent args)
    {
        if (OpenTerminal(args.User, ent)) args.Handled = true;
    }

    private void OnTerminalActivate(Entity<TarkovTerminalComponent> ent, ref ActivateInWorldEvent args)
    {
        if (OpenTerminal(args.User, ent)) args.Handled = true;
    }

    private bool OpenTerminal(EntityUid user, Entity<TarkovTerminalComponent> ent)
    {
        if (!Enabled || !TryComp<TarkovPlayerComponent>(user, out var player) || player.Closed || player.Raid != ""
            || !Alive(user) || !HasComp<TarkovHubComponent>(Transform(ent).MapUid)
            || !_interaction.InRangeUnobstructed(user, ent.Owner, range: 2.5f)) return false;
        if (player.ActiveTerminal != ent.Owner && player.ActiveTerminal is { } previous
            && TryComp<TarkovTerminalComponent>(previous, out var oldService) && oldService.Page == "trade")
            CancelUserTrade(player.User);
        player.ActiveTerminal = ent.Owner;
        if (ent.Comp.Page == "stash")
            foreach (var root in Roots(user)) TagTree(root, false);
        Feedback(player.User, TarkovFeedback.Confirm);
        if (TrySession(player.User, out var session)) SendState(session, "", ent.Comp.Page);
        return true;
    }

    private string ActiveService(EntityUid body)
    {
        var pc = Comp<TarkovPlayerComponent>(body);
        var wasTrade = pc.ActiveTerminal is { } previous && TryComp<TarkovTerminalComponent>(previous, out var old) && old.Page == "trade";
        if (pc.Closed || pc.Raid != "" || !Alive(body) || pc.ActiveTerminal is not { } target
            || !TryComp<TarkovTerminalComponent>(target, out var terminal)
            || !_interaction.InRangeUnobstructed(body, target, range: 2.5f)
            || !HasComp<TarkovHubComponent>(Transform(target).MapUid))
        {
            pc.ActiveTerminal = null;
            if (wasTrade) CancelUserTrade(pc.User);
            return "";
        }
        return terminal.Page;
    }

    private bool AtTradeService(string user, EntityUid requester)
        => TrySession(user, out var session) && session.Status == Robust.Shared.Enums.SessionStatus.InGame
            && FindPlayer(user) is { } body && ActiveService(body) == "trade"
            && Comp<TarkovPlayerComponent>(body).ActiveTerminal == Comp<TarkovPlayerComponent>(requester).ActiveTerminal;

    private string? CreateTestPartner(string owner)
    {
        if (!_cfg.GetCVar(TarkovCVars.TestBots) || _repository == null) return "tarkov-error-test-disabled";
        var data = _repository.Read();
        if (!data.Accounts.TryGetValue(owner, out var player) || player.Location != "hub") return "tarkov-error-hub";
        if (data.Accounts.Values.Any(a => a.TestBot && a.TestOwner == owner))
        {
            var existingPartner = data.Accounts.Values.FirstOrDefault(a => a.TestBot && a.TestOwner == owner && !a.TestTarget);
            if (existingPartner != null)
            {
                var restoredParty = player.Party == "" ? Guid.NewGuid().ToString("N") : player.Party;
                var restored = Write(owner, Guid.NewGuid().ToString("N"), "restore-test-party", d =>
                {
                    if (d.Accounts.Values.Count(a => a.Party == restoredParty && a.User != existingPartner.User) >= 4)
                        return "tarkov-error-party-full";
                    d.Accounts[owner].Party = restoredParty;
                    d.Accounts[existingPartner.User].Party = restoredParty;
                    return null;
                });
                if (restored != null) return restored;
                CancelReady(owner, "tarkov-queue-party-changed");
            }
            RestoreTestActors(owner);
            return null;
        }
        var partner = Guid.NewGuid().ToString();
        var target = Guid.NewGuid().ToString();
        var party = player.Party == "" ? Guid.NewGuid().ToString("N") : player.Party;
        var partnerProfile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithName(Loc.GetString("tarkov-test-partner"));
        partnerProfile = partnerProfile.WithCharacterAppearance(partnerProfile.Appearance.WithHairStyleName("HumanHairBob")
            .WithHairColor(Color.FromHex("#6E5039")));
        var targetProfile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithName(Loc.GetString("tarkov-test-target"));
        targetProfile = targetProfile.WithCharacterAppearance(targetProfile.Appearance.WithHairStyleName("HumanHairBedhead")
            .WithHairColor(Color.FromHex("#4D4337")).WithFacialHairStyleName("HumanFacialHair3oclock"));
        var error = Write(owner, "test-actors", "test", d =>
        {
            d.Accounts[owner].Party = party;
            TarkovEconomy.Create(d, partner, partnerProfile.Name, WriteProfile(partnerProfile), player.Faction, "Medic", 1000);
            TarkovEconomy.Create(d, target, targetProfile.Name, WriteProfile(targetProfile), "Exiles", "Assault", 0);
            d.Accounts[partner].TestBot = d.Accounts[target].TestBot = true;
            d.Accounts[partner].TestOwner = d.Accounts[target].TestOwner = owner;
            d.Accounts[target].TestTarget = true;
            d.Accounts[partner].Party = party;
            d.Accounts[partner].PendingKit = d.Accounts[target].PendingKit = "";
            return TarkovEconomy.CreateContract(d, new TarkovContract
            {
                Issuer = partner, Kind = "kill", Target = target, Reward = 500, EndsUtc = Math.Min(d.EndsUtc, Utc + 3600),
                Text = Loc.GetString("tarkov-test-bounty"),
            }, Utc);
        });
        if (error != null) return error;
        RestoreTestActors(owner);
        return null;
    }

    private void RestoreTestActors(string owner)
    {
        if (_repository == null || _hub == null) return;
        foreach (var account in _repository.Read().Accounts.Values.Where(a => a.TestBot && a.TestOwner == owner && a.Location == "hub"))
        {
            if (FindPlayer(account.User) != null) continue;
            var position = account.TestTarget ? new Vector2(2.5f, 0.5f) : new Vector2(-1.5f, 0.5f);
            var uid = _spawn.SpawnPlayerMob(new EntityCoordinates(HubCoordinates().EntityId, position), null, ReadProfile(account.Profile), null);
            _spawn.EquipStartingGear(uid, "TarkovStationCivilianGear");
            foreach (var root in Roots(uid)) TagTree(root, true);
            RemComp<Content.Shared.SSDIndicator.SSDIndicatorComponent>(uid);
            var pc = EnsureComp<TarkovPlayerComponent>(uid);
            pc.User = account.User;
            pc.Life = account.Life;
            pc.Faction = account.Faction;
            pc.Branch = account.Branch;
            EnsureComp<TarkovCombatCreditComponent>(uid);
            var bot = EnsureComp<TarkovTestBotComponent>(uid);
            bot.OwnerUser = owner;
            bot.Target = account.TestTarget;
            Dirty(uid, pc);
        }
    }

    private void UpdateTestBots()
    {
        if (_repository == null) return;
        var data = _repository.Read();
        if (_cfg.GetCVar(TarkovCVars.TestBots)) UpdateTestTrades(data);
        var bots = AllEntityQuery<TarkovTestBotComponent, TarkovPlayerComponent>();
        while (bots.MoveNext(out var uid, out var bot, out var player))
        {
            if (player.Closed || player.Raid == "" || !Alive(uid)) continue;
            if (data.Accounts.TryGetValue(bot.OwnerUser, out var owner) && owner.Location == "hub")
                CompleteExtraction(uid);
        }
    }

    private void UpdateTestTrades(TarkovData data)
    {
        // The optional NPC partner can approve a real escrow trade, so a solo tester can complete it.
        foreach (var trade in data.Trades.Values.Where(t => t.Status == "open"
            && (t.MoneyA > 0 || t.MoneyB > 0 || t.ItemsA.Count > 0 || t.ItemsB.Count > 0)))
        {
            var bots = AllEntityQuery<TarkovTestBotComponent, TarkovPlayerComponent>();
            while (bots.MoveNext(out _, out var bot, out var player))
            {
                if (bot.Target || player.Closed || (trade.A != player.User && trade.B != player.User)) continue;
                var human = trade.A == player.User ? trade.B : trade.A;
                if (human != bot.OwnerUser || !data.Accounts.TryGetValue(player.User, out var account)) continue;
                var items = human == trade.A ? trade.ItemsA : trade.ItemsB;
                var old = player.User == trade.A ? trade.MoneyA : trade.MoneyB;
                var accepted = player.User == trade.A ? trade.AcceptedA : trade.AcceptedB;
                var value = Math.Min(account.Balance + old, items.Where(data.Items.ContainsKey).Sum(i => data.Items[i].Emergency ? 0 : data.Items[i].Value));
                if (old == value && accepted) break;
                Write(player.User, Guid.NewGuid().ToString("N"), "test-trade", d =>
                {
                    if (old != value)
                    {
                        var error = TarkovEconomy.TradeMoney(d, player.User, trade.Id, value);
                        if (error != null) return error;
                    }
                    return TarkovEconomy.AcceptTrade(d, player.User, trade.Id);
                });
                break;
            }
        }
    }

    private void InitializeCommands() { }

    public string Admin(string[] args)
    {
        if (!Enabled || _repository == null || args.Length == 0) return "tarkov-error-starting";
        switch (args[0])
        {
            case "status":
                var d = _repository.Read();
                var status = $"TarkovStation cycle={d.Cycle} accounts={d.Accounts.Count} ends={d.EndsUtc} ready={_ready.Count}";
                var players = AllEntityQuery<TarkovPlayerComponent, TransformComponent>();
                while (players.MoveNext(out var uid, out var pc, out var transform))
                    if (!pc.Closed) status += $"\n{pc.User}: body={uid} raid={pc.Raid} map={transform.MapUid} pos={_transform.GetWorldPosition(uid)}";
                var exits = AllEntityQuery<TarkovExitComponent, TransformComponent>();
                while (exits.MoveNext(out _, out var exit, out var transform))
                    status += $"\nexit raid={exit.Raid} pos={_transform.GetWorldPosition(transform.Owner)}";
                return status;
            case "raidnow": _queueEnds = _timing.CurTime; return "tarkov-success";
            case "cycle" when args.Length == 2 && int.TryParse(args[1], out var seconds) && seconds >= 1:
                return Write("admin", Guid.NewGuid().ToString("N"), "cycle-time", data => { data.EndsUtc = Utc + seconds; return null; }) ?? "tarkov-success";
            case "grant" when args.Length == 3 && int.TryParse(args[2], out var amount) && amount is > 0 and <= 1000000:
                return Write("admin", Guid.NewGuid().ToString("N"), "grant", data =>
                {
                    if (!data.Accounts.TryGetValue(args[1], out var a)) return "tarkov-error-character";
                    a.Balance += amount;
                    return null;
                }) ?? "tarkov-success";
            case "partner" when args.Length == 2: return CreateTestPartner(args[1]) ?? "tarkov-success";
        }
        return "tarkov-error-request";
    }
}
