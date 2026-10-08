// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Linq;
using Content.Server._TarkovStation.Persistence;
using Content.Shared._TarkovStation.Components;
using Content.Shared._TarkovStation.Prototypes;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Clothing.Components;
using Content.Shared.Roles;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Medical.SuitSensors;
using Content.Shared.Inventory.VirtualItem;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._TarkovStation;

public sealed partial class TarkovSystem
{
    [Dependency] private IDependencyCollection _systemDependencies = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;

    private void InitializeItems()
    {
        EntityManager.EntityInitialized += OnRaidStackInitialized;
    }

    private void OnRaidStackInitialized(Entity<MetaDataComponent> ent)
    {
        if (Enabled && IsRaidPoster(ent.Comp)
            && HasComp<TarkovRaidComponent>(Transform(ent).MapUid))
        {
            QueueDel(ent);
            return;
        }
        if (!Enabled || !HasComp<StackComponent>(ent) || Goods(ent.Comp.EntityPrototype?.ID ?? "") == null
            || !TryComp<TarkovRaidComponent>(Transform(ent).MapUid, out var raid)
            || (TryComp<TarkovItemComponent>(ent, out var existing) && existing.Id != "")) return;
        TagTree(ent, false, raid.Id);
    }

    private void MarkHubSupplies()
    {
        var query = AllEntityQuery<ItemComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var transform))
            if (transform.MapUid == _hub && !HasComp<TarkovItemComponent>(uid)) TagTree(uid, true);
    }

    private IEnumerable<EntityUid> Roots(EntityUid body)
        => _inventory.GetHandOrInventoryEntities((body, null, null)).Where(uid => !HasComp<VirtualItemComponent>(uid)).Distinct();

    private List<EntityUid> ItemTree(EntityUid root)
    {
        var result = new List<EntityUid>();
        var stack = new Stack<EntityUid>();
        stack.Push(root);
        while (stack.TryPop(out var uid))
        {
            if (Deleted(uid))
                continue;
            // Loaded ammunition and nested bags are real entities too. Never
            // silently truncate their inventory at the former 512-entity cap.
            if (result.Count >= 4096)
                throw new InvalidOperationException("Item tree exceeds 4096 entities: " + MetaData(root).EntityPrototype?.ID);
            result.Add(uid);
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push(child);
        }
        return result;
    }

    private string SnapshotItem(EntityUid uid)
    {
        using var writer = new StringWriter();
        // Explicitly save the item tree with native mapping; exclude transient audio/effect descendants.
        var serializer = new EntitySerializer(_systemDependencies, new SerializationOptions
        {
            MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
            ErrorOnOrphan = false,
            Category = FileCategory.Entity,
        });
        var members = ItemTree(uid).ToHashSet();
        foreach (var transient in members.Where(member => !serializer.IsSerializable(member)).ToArray())
            members.ExceptWith(ItemTree(transient));
        serializer.SerializeEntities(members);
        var node = serializer.Write();
        if (node.TryGet("entities", out SequenceDataNode? groups))
        foreach (var group in groups.Cast<MappingDataNode>())
        foreach (var entity in group.Get<SequenceDataNode>("entities").Cast<MappingDataNode>())
        {
            if (!entity.TryGet("components", out SequenceDataNode? components)) continue;
            foreach (var component in components.Cast<MappingDataNode>())
                if (component.TryGet("type", out ValueDataNode? type) && type.Value == "UserInterface")
                    component.Remove("actors");
        }
        node.Write(writer);
        var result = writer.ToString();
        // A filled bag can legitimately exceed 512 KiB. Snapshots are stored in
        // dedicated SQLite blobs, not sent as one network packet.
        if (result.Length > 8 * 1024 * 1024)
            throw new InvalidOperationException($"Item snapshot too large: {MetaData(uid).EntityPrototype?.ID}, entities={members.Count}, chars={result.Length}");
        return result;
    }

    private EntityUid RestoreItem(TarkovStoredItem item)
    {
        using var reader = new StringReader(item.Snapshot);
        if (!_loader.TryLoadGeneric(reader, item.Prototype, out var loaded, new MapLoadOptions
            {
                MergeMap = _ticker.DefaultMap,
                DeserializationOptions = new DeserializationOptions { InitializeMaps = true, LogOrphanedGrids = false },
            }) || loaded == null)
            throw new InvalidOperationException("Item restore failed: " + item.Prototype);
        var entity = loaded.Entities.FirstOrDefault(uid => TryComp<TarkovItemComponent>(uid, out var tag) && tag.Id == item.Id);
        if (!entity.IsValid()) throw new InvalidOperationException("Item root ID missing after restore");
        _transform.SetCoordinates(entity, HubCoordinates());
        var tracked = EnsureComp<TarkovItemComponent>(entity);
        tracked.Id = item.Id;
        tracked.Emergency = item.Emergency;
        if (_repository != null)
        {
            var data = _repository.Read();
            foreach (var member in ItemTree(entity).Where(uid => uid != entity).ToArray())
            {
                if (!TryComp<TarkovItemComponent>(member, out var child)) continue;
                if (data.Items.TryGetValue(child.Id, out var canonical) && (canonical.Owner != item.Owner || canonical.Parent != item.Id))
                {
                    child.Id = "";
                    QueueDel(member);
                }
            }
        }
        // Reinsert after placing the tree on its actual map. Snapshot loading starts in nullspace;
        // the PVS/container metadata must be rebuilt on the playable map before replication.
        foreach (var member in ItemTree(entity))
        {
            if (!TryComp<ContainerManagerComponent>(member, out var manager)) continue;
            foreach (var container in manager.Containers.Values)
            foreach (var child in container.ContainedEntities.ToArray())
            {
                if (TryComp<TarkovItemComponent>(child, out var tag) && tag.Id == "") continue;
                if (_containers.Remove(child, container, force: true)) _containers.Insert(child, container, force: true);
            }
        }
        return entity;
    }

    private string SlotOf(EntityUid body, EntityUid item)
    {
        if (_inventory.TryGetSlots(body, out var slots))
            foreach (var slot in slots)
                if (_inventory.TryGetSlotEntity(body, slot.Name, out var equipped) && equipped == item) return slot.Name;
        return "";
    }

    private List<TarkovStoredItem> CaptureRoots(EntityUid body, string user, string location)
    {
        return CaptureItems(body, user, location, Roots(body));
    }

    private List<TarkovStoredItem> CaptureItems(EntityUid body, string user, string location, IEnumerable<EntityUid> roots)
    {
        var result = new List<TarkovStoredItem>();
        foreach (var root in roots)
        {
            TagTree(root, false);
            if (!TryComp<TarkovItemComponent>(root, out var tracked) || tracked.Id == "")
                continue;
            var members = ItemTree(root);
            var value = 0L;
            var containedValue = 0L;
            foreach (var member in members)
            {
                if (!TryComp<TarkovItemComponent>(member, out var tag))
                    continue;
                var proto = MetaData(member).EntityPrototype?.ID ?? "";
                var price = ItemPrice(member);
                value += price;
                if (member != root) containedValue += price;
                if (member != root)
                {
                    result.Add(new TarkovStoredItem
                    {
                        Id = tag.Id, Owner = user, Prototype = proto, Name = Name(member), Location = location,
                        Parent = tracked.Id, Emergency = tag.Emergency, FoundRaid = tag.FoundRaid,
                    });
                }
            }
            var quantity = TryComp<StackComponent>(root, out var rootStack) ? rootStack.Count : 1;
            var condition = ItemCondition(root);
            result.Add(new TarkovStoredItem
            {
                Quantity = quantity, Condition = condition,
                Id = tracked.Id, Owner = user, Prototype = MetaData(root).EntityPrototype?.ID ?? "",
                Name = Name(root), Snapshot = SnapshotItem(root), Location = location, Slot = SlotOf(body, root),
                Emergency = tracked.Emergency, FoundRaid = tracked.FoundRaid, Value = value, ContainedValue = containedValue,
            });
        }
        return result;
    }

    private long ItemPrice(EntityUid uid)
    {
        if (TryComp<TarkovItemComponent>(uid, out var tag) && tag.Emergency) return 0;
        var goods = Goods(MetaData(uid).EntityPrototype?.ID ?? "");
        if (goods == null)
        {
            // Portable expedition scrap has a small salvage value. Free hub items
            // and untracked internal gun entities do not gain value from this fallback.
            return HasComp<ItemComponent>(uid) && !HasComp<CartridgeAmmoComponent>(uid) && tag?.FoundRaid != null && tag.FoundRaid != ""
                ? TryComp<StackComponent>(uid, out var scrap) ? Math.Clamp(scrap.Count, 0, 10000) : 5 : 0;
        }
        var price = (long)goods.Sell;
        if (goods.PricedSolution is { } solutionName
            && _solutions.TryGetSolution(uid, solutionName, out _, out var solution)
            && _proto.Index(goods.Product).TryComp<SolutionContainerManagerComponent>(out var initial, EntityManager.ComponentFactory)
            && initial.Solutions is { } prototypeSolutions && prototypeSolutions.TryGetValue(solutionName, out var prototypeSolution))
        {
            var maximum = prototypeSolution.Volume.Float();
            if (maximum > 0) price = 2 + (long)(Math.Max(0, price - 2) * Math.Clamp(solution.Volume.Float() / maximum, 0, 1));
        }
        if (TryComp<StackComponent>(uid, out var stack)
            && _proto.Index(goods.Product).TryComp<StackComponent>(out var unit, EntityManager.ComponentFactory))
            price = price * stack.Count / Math.Max(1, unit.Count);
        // Magazines keep a small empty-case value; their ammunition is part of the buyback quote.
        if (HasComp<BallisticAmmoProviderComponent>(uid) && !HasComp<GunComponent>(uid))
        {
            var ammunition = new GetAmmoCountEvent(); RaiseLocalEvent(uid, ref ammunition);
            if (ammunition.Capacity > 0)
            {
                var emptyValue = price / 4;
                price = emptyValue + (price - emptyValue) * Math.Clamp(ammunition.Count, 0, ammunition.Capacity) / ammunition.Capacity;
            }
        }
        return price;
    }

    private string ItemCondition(EntityUid uid)
    {
        if (!HasComp<GunComponent>(uid) && !HasComp<BallisticAmmoProviderComponent>(uid)) return "";
        var ammunition = new GetAmmoCountEvent(); RaiseLocalEvent(uid, ref ammunition);
        return Loc.GetString("ts-item-ammo", ("count", ammunition.Count), ("max", ammunition.Capacity));
    }

    private TarkovStoredItem LiveItem(EntityUid uid)
    {
        var tag = Comp<TarkovItemComponent>(uid);
        var members = ItemTree(uid);
        return new TarkovStoredItem
        {
            Id = tag.Id, Name = Name(uid), Prototype = MetaData(uid).EntityPrototype?.ID ?? "",
            Emergency = tag.Emergency, FoundRaid = tag.FoundRaid,
            Quantity = TryComp<StackComponent>(uid, out var stack) ? stack.Count : 1,
            Condition = ItemCondition(uid), Value = members.Sum(ItemPrice),
            ContainedValue = members.Where(m => m != uid).Sum(ItemPrice),
        };
    }

    private TarkovGoodsPrototype? Goods(string prototype)
    {
        var goods = _proto.EnumeratePrototypes<TarkovGoodsPrototype>().ToArray();
        var exact = goods.FirstOrDefault(g => g.Product.Id == prototype);
        if (exact != null) return exact;
        // Splitting a stack spawns its base prototype (SheetSteel rather than SheetSteel10).
        if (!_proto.TryIndex<EntityPrototype>(prototype, out var entity)
            || !entity.TryComp<StackComponent>(out var stack, EntityManager.ComponentFactory)) return null;
        return goods.FirstOrDefault(g => _proto.Index(g.Product).TryComp<StackComponent>(out var unit, EntityManager.ComponentFactory)
            && unit.StackTypeId == stack.StackTypeId);
    }

    private void TagTree(EntityUid root, bool emergency, string raid = "")
    {
        foreach (var uid in ItemTree(root))
        {
            // Station suit tracking is outside this mode and carries references to transient wearers/servers.
            RemComp<SuitSensorComponent>(uid);
            // Storage must retain unpriced personal items as well as the merchant's catalogue.
            // Internal gun/chamber containers are excluded to avoid tracking every bullet as a separate item.
            if (uid != root && Goods(MetaData(uid).EntityPrototype?.ID ?? "") == null
                && !(HasComp<ItemComponent>(uid) && _containers.TryGetContainingContainer(uid, out var storage)
                    && storage.ID == StorageComponent.ContainerId))
                continue;
            var tag = EnsureComp<TarkovItemComponent>(uid);
            if (tag.Id == "")
            {
                tag.Id = Guid.NewGuid().ToString("N");
                if (raid == "" && TryComp<TarkovRaidComponent>(Transform(uid).MapUid, out var source)) tag.FoundRaid = source.Id;
            }
            tag.Emergency |= emergency;
            if (raid != "")
                tag.FoundRaid = raid;
            Dirty(uid, tag);
        }
    }

    private void CapturePlayer(string user)
    {
        if (_repository == null || FindPlayer(user) is not { } body)
            return;
        var data = _repository.Read();
        if (!data.Accounts.TryGetValue(user, out var account) || account.Location != "hub")
            return;
        var items = CaptureRoots(body, user, "hub");
        Write(user, Guid.NewGuid().ToString("N"), "checkpoint", d =>
        {
            var ids = items.Select(i => i.Id).ToHashSet();
            foreach (var previous in d.Items.Values.Where(i => i.Owner == user && i.Location == "hub" && !ids.Contains(i.Id)))
                previous.Location = "loose";
            foreach (var item in items)
                d.Items[item.Id] = item;
            return null;
        });
    }

    private void CheckpointAll()
    {
        if (_repository == null)
            return;
        foreach (var user in _repository.Read().Accounts.Keys)
        {
            try { CapturePlayer(user); }
            catch (Exception exception) { Log.Error($"Tarkov checkpoint failed: {exception}"); }
        }
    }

    private EntityUid? FindItem(string id)
    {
        var query = AllEntityQuery<TarkovItemComponent>();
        while (query.MoveNext(out var uid, out var item))
        {
            if (item.Id == id && !Deleted(uid))
                return uid;
        }
        return null;
    }

    private void RestoreEquipment(EntityUid body, string user)
    {
        if (_repository == null)
            return;
        var data = _repository.Read();
        foreach (var record in data.Items.Values.Where(i => i.Owner == user && i.Location == "hub" && i.Parent == "")
                     .OrderBy(i => i.Slot == "" ? 1 : 0))
        {
            if (FindItem(record.Id) != null)
                continue;
            var uid = RestoreItem(record);
            if (record.Slot == "" || !_inventory.TryEquip(body, uid, record.Slot, silent: true, force: true))
                _hands.PickupOrDrop(body, uid);
        }
        if (data.Accounts.TryGetValue(user, out var account) && account.PendingKit != "")
        {
            var error = DeliverKit(user, account.PendingKit, "starter-" + user, free: true);
            if (error == null)
            {
                foreach (var item in _repository.Read().Items.Values.Where(i => i.Owner == user && i.Location == "stash" && i.Parent == "").OrderBy(i => i.Slot == "" ? 1 : 0).ToArray())
                    Withdraw(body, user, item.Id, Guid.NewGuid().ToString("N"));
            }
        }
    }

    private string? DeliverKit(string user, string kitId, string operation, bool free = false)
    {
        if (_repository == null || !_proto.TryIndex<TarkovKitPrototype>(kitId, out var kit))
            return "tarkov-error-kit";
        var before = _repository.Read();
        if (!before.Accounts.TryGetValue(user, out var account) || account.Location != "hub")
            return "tarkov-error-hub";
        if (free && account.PendingKit != kitId)
            return "tarkov-error-replay";
        if (!free && !kit.Emergency && account.Balance < kit.Price)
            return "tarkov-error-money";
        var temporary = Spawn("MobHuman", HubCoordinates());
        try
        {
            _spawn.EquipStartingGear(temporary, kit.Gear);
            foreach (var root in Roots(temporary))
                TagTree(root, kit.Emergency);
            var items = CaptureRoots(temporary, user, "stash");
            var rootCount = items.Count(i => i.Parent == "");
            return Write(user, operation, "kit", d =>
            {
                var player = d.Accounts[user];
                if (player.Location != "hub" || (free && player.PendingKit != kitId))
                    return "tarkov-error-hub";
                if (d.Items.Values.Count(i => i.Owner == user && i.Location == "stash" && i.Parent == "") + rootCount > 64)
                    return "tarkov-error-stash-full";
                if (kit.Emergency)
                {
                    if (player.LastEmergencyUtc + 120 > Utc || player.Balance >= 500 || TarkovEconomy.Wealth(d, player) >= 1000)
                        return "tarkov-error-emergency";
                    player.LastEmergencyUtc = Utc;
                }
                else if (!free)
                {
                    var error = TarkovEconomy.Debit(d, user, kit.Price);
                    if (error != null) return error;
                }
                foreach (var item in items)
                    d.Items[item.Id] = item;
                if (free) player.PendingKit = "";
                return null;
            });
        }
        finally { Del(temporary); }
    }

    private string? Buy(string user, string goodsId, string operation)
    {
        if (_repository == null || !_proto.TryIndex<TarkovGoodsPrototype>(goodsId, out var goods) || !goods.Purchasable || goods.Buy <= 0)
            return "tarkov-error-item";
        var temporary = Spawn("MobHuman", HubCoordinates());
        try
        {
            var item = Spawn(goods.Product, Transform(temporary).Coordinates);
            TagTree(item, false);
            _hands.PickupOrDrop(temporary, item);
            var records = CaptureRoots(temporary, user, "stash");
            return Write(user, operation, "buy", data =>
            {
                if (data.Accounts[user].Location != "hub") return "tarkov-error-hub";
                if (data.Items.Values.Count(i => i.Owner == user && i.Location == "stash" && i.Parent == "") >= 64)
                    return "tarkov-error-stash-full";
                var error = TarkovEconomy.Debit(data, user, goods.Buy);
                if (error != null) return error;
                foreach (var record in records) data.Items[record.Id] = record;
                return null;
            });
        }
        finally { Del(temporary); }
    }

    /// <summary>Only carried roots and native storage contents, never magazines, implants or nearby items.</summary>
    private List<(EntityUid Item, EntityUid? Container, string Path)> CarriedItems(EntityUid body)
    {
        var result = new List<(EntityUid Item, EntityUid? Container, string Path)>();
        var pending = new Queue<(EntityUid Item, EntityUid? Container, string Path)>();
        foreach (var root in Roots(body)) pending.Enqueue((root, null, ""));
        var seen = new HashSet<EntityUid>();
        while (pending.TryDequeue(out var entry) && result.Count < 512)
        {
            if (Deleted(entry.Item) || EntityManager.IsQueuedForDeletion(entry.Item) || !seen.Add(entry.Item)) continue;
            result.Add(entry);
            if (!TryComp<StorageComponent>(entry.Item, out var storage)) continue;
            var path = entry.Path == "" ? Name(entry.Item) : entry.Path + " / " + Name(entry.Item);
            foreach (var child in storage.Container.ContainedEntities)
                pending.Enqueue((child, entry.Item, path));
        }
        return result;
    }

    private string? Deposit(EntityUid body, string user, string id, string operation)
    {
        var selected = CarriedItems(body).FirstOrDefault(entry => TryComp<TarkovItemComponent>(entry.Item, out var tag) && tag.Id == id);
        var root = selected.Item;
        if (!root.IsValid()) return "tarkov-error-item";
        var records = CaptureItems(body, user, "stash", new[] { root });
        var detached = false;
        var committed = false;
        try
        {
            if (selected.Container is { } container)
            {
                if (!TryComp<StorageComponent>(container, out var storage)
                    || !_containers.Remove(root, storage.Container, destination: Transform(body).Coordinates))
                    return "tarkov-error-item";
                detached = true;
            }
            // Capture the changed parent in the same transaction: its snapshot/value must not retain the child.
            var remaining = detached ? CaptureRoots(body, user, "hub") : new List<TarkovStoredItem>();
            var error = Write(user, operation, "deposit", data =>
            {
                if (data.Accounts[user].Location != "hub") return "tarkov-error-hub";
                if (data.Items.Values.Count(i => i.Owner == user && i.Location == "stash" && i.Parent == "") >= 64)
                    return "tarkov-error-stash-full";
                if (data.Items.TryGetValue(id, out var old)
                    && (old.Owner != user || old.Location is "stash" or "trade" or "gone")) return "tarkov-error-item";
                foreach (var record in remaining) data.Items[record.Id] = record;
                foreach (var record in records) data.Items[record.Id] = record;
                return null;
            });
            if (error != null) return error;
            committed = true;
            // Clear retiring projection IDs before same-tick withdrawal or native containment events.
            foreach (var member in ItemTree(root))
                if (TryComp<TarkovItemComponent>(member, out var tag)) tag.Id = "";
            QueueDel(root);
            return null;
        }
        finally
        {
            if (detached && !committed && selected.Container is { } container)
                _storage.Insert(container, root, out _, user: body, playSound: false, stackAutomatically: false);
        }
    }

    private string? Withdraw(EntityUid body, string user, string id, string operation)
    {
        if (_repository == null) return "tarkov-error-starting";
        var before = _repository.Read();
        if (!before.Items.TryGetValue(id, out var record) || record.Owner != user || record.Parent != "" || record.Location != "stash")
            return "tarkov-error-item";
        // There can be only one live projection of a durable item ID.
        if (FindItem(id) != null) return "tarkov-error-item";
        // Native placement can fail (occupied slot/full hands). Never turn that into a successful ground drop.
        var item = RestoreItem(record);
        // The physical stash may be several tiles from the hub spawn. Native equip checks item reach.
        _transform.SetCoordinates(item, Transform(body).Coordinates);
        var placed = record.Slot != "" && _inventory.TryEquip(body, item, record.Slot, silent: true);
        if (!placed && !HasComp<GunComponent>(item) && TryComp<ClothingComponent>(item, out var clothing) && _inventory.TryGetSlots(body, out var slots))
        {
            foreach (var slot in slots)
            {
                if ((slot.SlotFlags & clothing.Slots) == 0 || _inventory.TryGetSlotEntity(body, slot.Name, out _)) continue;
                if (!_inventory.TryEquip(body, item, slot.Name, silent: true)) continue;
                placed = true;
                break;
            }
        }
        if (!placed) placed = _hands.TryPickupAnyHand(body, item, checkActionBlocker: false, animate: false);
        Log.Info($"Stash projection placed: user={user}, item={record.Prototype}, preferred={record.Slot}, actual={SlotOf(body, item)}, placed={placed}");
        if (!placed)
        {
            Log.Warning($"Stash withdrawal has no placement: user={user}, item={record.Prototype}, preferredSlot={record.Slot}");
            RetireProjection(item);
            return "tarkov-error-hands-full";
        }
        var error = Write(user, operation, "withdraw", data =>
        {
            if (data.Accounts[user].Location != "hub") return "tarkov-error-hub";
            var moved = TarkovEconomy.Move(data, user, id, "stash", "hub");
            if (moved == null) data.Items[id].Slot = SlotOf(body, item);
            return moved;
        });
        if (error != null) RetireProjection(item);
        return error;
    }

    private void RetireProjection(EntityUid root)
    {
        foreach (var member in ItemTree(root))
            if (TryComp<TarkovItemComponent>(member, out var tag)) tag.Id = "";
        if (_containers.TryGetContainingContainer(root, out var container)) _containers.Remove(root, container, force: true);
        QueueDel(root);
    }
}
