// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._TarkovStation.Components;
using Content.Shared._TarkovStation.Prototypes;
using Robust.Shared.Prototypes;
using Content.Shared.Damage.Events;
using Content.Shared.Mobs.Components;
using Content.Shared.Construction.Components;
using Content.Goobstation.Common.Stunnable;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.Interaction.Events;
using Content.Shared.Throwing;
using Content.Shared.Flash;
using Content.Shared.Movement.Pulling.Events;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;

namespace Content.Shared._TarkovStation;

/// <summary>Shared hub interaction policy; the same checks also run in client prediction.</summary>
public sealed partial class TarkovHubPolicySystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TarkovHubProtectedComponent, BeforeDamageChangedEvent>(OnDamage);
        SubscribeLocalEvent<TarkovPlayerComponent, AttackAttemptEvent>(OnAttack);
        SubscribeLocalEvent<TarkovHubProtectedComponent, BeforeStaminaDamageEvent>(OnStamina);
        SubscribeLocalEvent<TarkovHubProtectedComponent, BeforeStunEvent>(OnStun);
        SubscribeLocalEvent<TarkovHubProtectedComponent, UnanchorAttemptEvent>(OnUnanchor);
        SubscribeLocalEvent<TarkovHubProtectedComponent, BeforeKnockdownEvent>(OnKnockdown);
        SubscribeLocalEvent<TarkovHubProtectedComponent, FlashAttemptEvent>(OnFlash);
        SubscribeLocalEvent<TarkovPlayerComponent, PullAttemptEvent>(OnPull);
        SubscribeLocalEvent<TarkovHubProtectedComponent, ShotAttemptedEvent>(OnShot);
        SubscribeLocalEvent<TarkovPlayerComponent, BeforeThrowEvent>(OnThrow);
        SubscribeLocalEvent<TarkovPlayerComponent, InteractionAttemptEvent>(OnInteract);
        SubscribeLocalEvent<TarkovPlayerComponent, ExaminedEvent>(OnExamined);
    }

    private bool Hub(EntityUid uid) => TryComp(uid, out TransformComponent? transform)
        && HasComp<TarkovHubComponent>(transform.MapUid);

    private void OnDamage(Entity<TarkovHubProtectedComponent> ent, ref BeforeDamageChangedEvent args)
    {
        if (Hub(ent) && args.Damage.AnyPositive()) args.Cancelled = true;
    }

    private void OnUnanchor(Entity<TarkovHubProtectedComponent> ent, ref UnanchorAttemptEvent args)
    {
        if (Hub(ent)) args.Cancel();
    }

    private void OnKnockdown(Entity<TarkovHubProtectedComponent> ent, ref BeforeKnockdownEvent args)
    {
        if (Hub(ent)) args.Cancelled = true;
    }

    private void OnFlash(Entity<TarkovHubProtectedComponent> ent, ref FlashAttemptEvent args)
    {
        if (Hub(ent)) args.Cancelled = true;
    }

    private void OnPull(Entity<TarkovPlayerComponent> ent, ref PullAttemptEvent args)
    {
        if (Hub(ent) && HasComp<MobStateComponent>(args.PulledUid)) args.Cancelled = true;
    }

    private void OnStamina(Entity<TarkovHubProtectedComponent> ent, ref BeforeStaminaDamageEvent args)
    {
        if (Hub(ent)) args.Cancelled = true;
    }

    private void OnStun(Entity<TarkovHubProtectedComponent> ent, ref BeforeStunEvent args)
    {
        if (Hub(ent)) args.Cancelled = true;
    }

    private void OnAttack(Entity<TarkovPlayerComponent> ent, ref AttackAttemptEvent args)
    {
        if (Hub(ent)) args.Cancel();
    }

    private void OnShot(Entity<TarkovHubProtectedComponent> ent, ref ShotAttemptedEvent args)
    {
        if (Hub(args.User)) args.Cancel();
    }

    private void OnThrow(Entity<TarkovPlayerComponent> ent, ref BeforeThrowEvent args)
    {
        if (Hub(ent)) args.Cancelled = true;
    }

    private void OnInteract(Entity<TarkovPlayerComponent> ent, ref InteractionAttemptEvent args)
    {
        if (Hub(ent) && args.Target != null && args.Target != ent.Owner && HasComp<TarkovPlayerComponent>(args.Target))
            args.Cancelled = true;
    }

    private void OnExamined(Entity<TarkovPlayerComponent> ent, ref ExaminedEvent args)
    {
        var faction = _prototypes.TryIndex<TarkovFactionPrototype>(ent.Comp.Faction, out var f) ? Loc.GetString(f.Name) : ent.Comp.Faction;
        var branch = _prototypes.TryIndex<TarkovKitPrototype>(ent.Comp.Branch, out var k) ? Loc.GetString(k.Name) : ent.Comp.Branch;
        args.PushMarkup(Loc.GetString("tarkov-examine-faction", ("faction", faction), ("branch", branch)));
    }
}
