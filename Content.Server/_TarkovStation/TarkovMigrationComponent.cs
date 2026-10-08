// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;

namespace Content.Server._TarkovStation;

/// <summary>Native HTN follows these raid-local waypoints whenever no hostile target is in reach.</summary>
[RegisterComponent]
public sealed partial class TarkovMigrationComponent : Component
{
    [DataField] public EntityUid RaidMap;
    [DataField] public Vector2[] Route = Array.Empty<Vector2>();
    [DataField] public int Waypoint;
}
