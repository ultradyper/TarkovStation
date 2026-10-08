// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Shared._TarkovStation.Prototypes;

[Prototype]
public sealed partial class TarkovGoodsPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = "";
    [DataField(required: true)] public EntProtoId Product;
    [DataField] public int Buy = 100;
    [DataField] public int Sell = 40;
    [DataField] public LocId Category = "ts-category-supplies";
    [DataField] public int Rarity;
    [DataField] public bool Purchasable = true;
    /// <summary>Consumable solution whose remaining volume scales the buyback quote.</summary>
    [DataField] public string? PricedSolution;
}
