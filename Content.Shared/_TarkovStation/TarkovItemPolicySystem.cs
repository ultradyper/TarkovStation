// SPDX-License-Identifier: AGPL-3.0-or-later
using Content.Shared._TarkovStation.Components;
using Content.Shared.Examine;
using Content.Shared.Stacks;
using Robust.Shared.Network;

namespace Content.Shared._TarkovStation;

/// <summary>Replicated provenance follows predicted splits and remains visible when examining an item.</summary>
public sealed partial class TarkovItemPolicySystem : EntitySystem
{
    [Dependency] private INetManager _net = default!;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TarkovItemComponent, StackSplitEvent>(OnSplit);
        SubscribeLocalEvent<TarkovItemComponent, ExaminedEvent>(OnExamined);
    }

    private void OnSplit(Entity<TarkovItemComponent> ent, ref StackSplitEvent args)
    {
        var item = EnsureComp<TarkovItemComponent>(args.NewId);
        // Only the authoritative server creates durable IDs; prediction copies visible provenance.
        if (_net.IsServer) item.Id = Guid.NewGuid().ToString("N");
        item.Emergency = ent.Comp.Emergency;
        item.FoundRaid = ent.Comp.FoundRaid;
        Dirty(args.NewId, item);
    }

    private void OnExamined(Entity<TarkovItemComponent> ent, ref ExaminedEvent args)
    {
        if (ent.Comp.Emergency) args.PushMarkup(Loc.GetString("tarkov-loan"));
        else if (ent.Comp.FoundRaid != "") args.PushMarkup(Loc.GetString("tarkov-found"));
    }
}
