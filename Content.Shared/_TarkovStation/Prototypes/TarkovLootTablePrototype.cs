// SPDX-License-Identifier: AGPL-3.0-or-later
using Robust.Shared.Prototypes;

namespace Content.Shared._TarkovStation.Prototypes;

/// <summary>Budgeted weighted supplies for a recognizable raid zone.</summary>
[Prototype]
public sealed partial class TarkovLootTablePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = "";
    [DataField] public int Rolls = 3;
    [DataField] public int Budget = 1000;
    [DataField(required: true)] public List<TarkovLootEntry> Entries = new();
}

[DataDefinition]
public sealed partial class TarkovLootEntry
{
    [DataField(required: true)] public ProtoId<TarkovGoodsPrototype> Goods;
    [DataField] public int Weight = 1;
}
