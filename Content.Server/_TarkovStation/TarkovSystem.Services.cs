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
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._TarkovStation;

public sealed partial class TarkovSystem
{
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedCombatModeSystem _combatMode = default!;
    [Dependency] private MobStateSystem _mobState = default!;
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
        // Succumb and rage quit use the ghost command. Resolve death here before
        // blocking observer creation, otherwise both buttons leave players in crit forever.
        if (args.Mind.CurrentEntity is not { } body
            || !TryComp<TarkovPlayerComponent>(body, out var player)
            || player.Closed || player.Raid == "") return;
        if (_mobState.IsCritical(body))
        {
            _mobState.ChangeMobState(body, MobState.Dead);
            args.Result = true;
        }
        else if (_mobState.IsDead(body))
        {
            CloseLife(body);
            args.Result = true;
        }
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

}
