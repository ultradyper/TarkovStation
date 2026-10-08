// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Shared._TarkovStation.Components;
using Content.Shared._TarkovStation.Prototypes;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._TarkovStation;

/// <summary>Equipment and looting extensions to native HTN. Never creates replacement ammunition or loot.</summary>
public sealed partial class TarkovRaiderSystem : EntitySystem
{
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedGunSystem _guns = default!;
    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private SharedEntityStorageSystem _crates = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IGameTiming _timing = default!;

    private IEnumerable<EntityUid> Equipment(EntityUid owner)
    {
        var queue = new Queue<EntityUid>(_inventory.GetHandOrInventoryEntities((owner, null, null)));
        var seen = new HashSet<EntityUid>();
        while (queue.TryDequeue(out var uid) && seen.Count < 128)
        {
            if (Deleted(uid) || !seen.Add(uid)) continue;
            yield return uid;
            if (TryComp<StorageComponent>(uid, out var bag))
                foreach (var child in bag.Container.ContainedEntities) queue.Enqueue(child);
        }
    }

    private int Ammunition(EntityUid uid)
    {
        var count = new GetAmmoCountEvent();
        RaiseLocalEvent(uid, ref count);
        return count.Count;
    }

    public bool NeedsWeaponService(EntityUid owner)
        => HasComp<TarkovRaiderComponent>(owner) && _hands.GetActiveItem(owner) is { } held
            && HasComp<GunComponent>(held) && (Ammunition(held) == 0 || NeedsChambering(held));

    private bool NeedsChambering(EntityUid gun)
    {
        if (!TryComp<ChamberMagazineAmmoProviderComponent>(gun, out var provider)) return false;
        return provider.BoltClosed == false || _guns.GetChamberEntity(gun) is not { } chamber
            || (TryComp<CartridgeAmmoComponent>(chamber, out var cartridge) && cartridge.Spent);
    }

    public HTNOperatorStatus MaintainWeapon(EntityUid owner)
    {
        if (!TryComp<TarkovRaiderComponent>(owner, out var raider) || _hands.GetActiveItem(owner) is not { } gun
            || !HasComp<GunComponent>(gun)) return HTNOperatorStatus.Failed;
        if (Ammunition(gun) > 0)
        {
            // Stock GunCanFire checks Magazine before ChamberMagazine: loaded magazines can mask
            // an open bolt or empty chamber. Rack the actual weapon instead of attempting empty shots forever.
            if (NeedsChambering(gun)) RaiseLocalEvent(gun, new UseInHandEvent(owner));
            raider.ReloadAt = TimeSpan.Zero;
            return HTNOperatorStatus.Finished;
        }
        var spare = EntityUid.Invalid;
        if (_slots.TryGetSlot(gun, SharedGunSystem.MagazineSlot, out var slot))
            spare = Equipment(owner).FirstOrDefault(item => item != gun && Ammunition(item) > 0
                && _slots.CanInsert(gun, item, owner, slot, swap: true));
        if (!spare.IsValid())
        {
            raider.ReloadAt = TimeSpan.Zero;
            var knife = Equipment(owner).FirstOrDefault(item => MetaData(item).EntityPrototype?.ID == "CombatKnife");
            if (!knife.IsValid() || !_hands.TryDrop(owner, gun)) return HTNOperatorStatus.Failed;
            // Empty weapons remain real loot. The carried knife becomes the active melee weapon.
            if (!_hands.TryPickup(owner, knife)) return HTNOperatorStatus.Failed;
            raider.KnifeMode = true;
            return HTNOperatorStatus.Finished;
        }
        if (raider.ReloadAt == TimeSpan.Zero)
            raider.ReloadAt = _timing.CurTime + TimeSpan.FromSeconds(Math.Clamp(raider.ReloadSeconds, 0.5f, 10f));
        if (_timing.CurTime < raider.ReloadAt) return HTNOperatorStatus.Continuing;
        if (_guns.GetMagazineEntity(gun) != null)
        {
            if (!_slots.TryEject(gun, SharedGunSystem.MagazineSlot, owner, out var empty, doAfter: false))
                return HTNOperatorStatus.Failed;
            if (empty != null && _hands.IsHolding(owner, empty.Value)) _hands.TryDrop(owner, empty.Value);
        }
        if (!_slots.TryInsert(gun, SharedGunSystem.MagazineSlot, spare, owner)) return HTNOperatorStatus.Failed;
        // Native use cycles the chamber/bolt and plays the weapon's own rack sound.
        RaiseLocalEvent(gun, new UseInHandEvent(owner));
        raider.ReloadAt = TimeSpan.Zero;
        raider.Reloads++;
        return HTNOperatorStatus.Finished;
    }

    public void IgnoreLoot(EntityUid owner, EntityUid target)
    {
        if (TryComp<TarkovRaiderComponent>(owner, out var raider))
            raider.IgnoredLoot[target] = _timing.CurTime + TimeSpan.FromSeconds(30);
    }

    public EntityUid? FindLoot(EntityUid owner)
    {
        if (!TryComp<TarkovRaiderComponent>(owner, out var raider) || _timing.CurTime < raider.NextLoot
            || !HasComp<TarkovRaidComponent>(Transform(owner).MapUid)) return null;
        var bags = Equipment(owner).Where(item => HasComp<StorageComponent>(item)).ToArray();
        if (bags.Length == 0) return null;
        foreach (var (uid, expiry) in raider.IgnoredLoot.ToArray())
            if (Deleted(uid) || expiry <= _timing.CurTime) raider.IgnoredLoot.Remove(uid);
        EntityUid? target = null;
        var best = float.MinValue;
        foreach (var uid in _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(owner), raider.SearchRange))
        {
            if (Deleted(uid) || uid == owner || raider.IgnoredLoot.ContainsKey(uid)
                || _containers.TryGetContainingContainer(uid, out _) || Transform(uid).MapUid != Transform(owner).MapUid
                || !TryComp<TarkovItemComponent>(uid, out var item) || item.Emergency || item.FoundRaid == "") continue;
            var crate = HasComp<EntityStorageComponent>(uid);
            if (!crate && (!HasComp<ItemComponent>(uid) || Transform(uid).Anchored
                || !bags.Any(bag => _storage.CanInsert(bag, uid, out _, ignoreLocation: true)))) continue;
            // No wall vision and no access to inventories of players or other inhabitants.
            if (!_interaction.InRangeUnobstructed(owner, uid, range: raider.SearchRange)) continue;
            var distance = (_transform.GetWorldPosition(uid) - _transform.GetWorldPosition(owner)).Length();
            var score = (crate ? 120f : 70f) / (1 + distance);
            if (score <= best) continue;
            best = score;
            target = uid;
        }
        return target;
    }

    public bool CollectLoot(EntityUid owner, EntityUid target)
    {
        if (!TryComp<TarkovRaiderComponent>(owner, out var raider) || Deleted(target)
            || !_interaction.InRangeUnobstructed(owner, target, range: 1.8f)) return false;
        var candidates = new List<EntityUid>();
        if (TryComp<EntityStorageComponent>(target, out var crate))
        {
            candidates.AddRange(crate.Contents.ContainedEntities);
            if (!crate.Open && !_crates.TryOpenStorage(owner, target)) { IgnoreLoot(owner, target); return false; }
            candidates.AddRange(_lookup.GetEntitiesInRange(_transform.GetMapCoordinates(target), 1.5f)
                .Where(uid => HasComp<ItemComponent>(uid) && !Transform(uid).Anchored
                    && !_containers.TryGetContainingContainer(uid, out _)));
        }
        else if (HasComp<ItemComponent>(target) && !_containers.TryGetContainingContainer(target, out _))
            candidates.Add(target);
        var bags = Equipment(owner).Where(item => HasComp<StorageComponent>(item)).ToArray();
        // Native items are moved, not copied; the player may later loot the inhabitant's backpack.
        var count = 0;
        foreach (var item in candidates.Distinct())
        {
            if (count >= 2 || Deleted(item) || !TryComp<TarkovItemComponent>(item, out var tag) || tag.Emergency
                || tag.FoundRaid == "" || !_interaction.InRangeUnobstructed(owner, item, range: 2f)) continue;
            foreach (var bag in bags)
            {
                if (!_storage.Insert(bag, item, out _, user: owner, stackAutomatically: false)) continue;
                count++;
                raider.LootedItems++;
                break;
            }
        }
        raider.NextLoot = _timing.CurTime + TimeSpan.FromSeconds(5);
        IgnoreLoot(owner, target);
        return count > 0;
    }
}
