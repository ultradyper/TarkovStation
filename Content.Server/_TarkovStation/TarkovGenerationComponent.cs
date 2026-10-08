// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Threading.Tasks;
using Content.Shared.Procedural;

namespace Content.Server._TarkovStation;

/// <summary>Runtime generation jobs owned by a raid map, discarded when that map is removed.</summary>
[RegisterComponent]
public sealed partial class TarkovGenerationComponent : Component
{
    public List<Task<List<Dungeon>>> Jobs = new();
    public Dictionary<string, string> PartyGroups = new();
    public TimeSpan Deadline;
    public bool Cancelled;
}
