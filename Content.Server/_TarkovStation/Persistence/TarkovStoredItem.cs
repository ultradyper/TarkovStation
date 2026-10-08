// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._TarkovStation.Persistence;

public sealed class TarkovStoredItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Owner { get; set; } = "";
    public string Prototype { get; set; } = "";
    public string Name { get; set; } = "";
    public string Snapshot { get; set; } = "";
    public string Location { get; set; } = "stash";
    public string Parent { get; set; } = "";
    public string Slot { get; set; } = "";
    public string FoundRaid { get; set; } = "";
    public bool Emergency { get; set; }
    public long Value { get; set; }
    /// <summary>Owned contents excluding the container itself; loan containers must not hide valuable loot.</summary>
    public long ContainedValue { get; set; }
    public int Quantity { get; set; } = 1;
    public string Condition { get; set; } = "";

    internal TarkovStoredItem Copy() => (TarkovStoredItem)MemberwiseClone();
}
