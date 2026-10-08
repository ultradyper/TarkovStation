// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Shared._TarkovStation.Prototypes;

[Prototype]
public sealed partial class TarkovFactionPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = "";
    [DataField(required: true)] public LocId Name;
    [DataField(required: true)] public LocId Description;
}
