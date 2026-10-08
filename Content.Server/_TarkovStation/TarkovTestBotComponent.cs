// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Threading.Tasks;
using Content.Shared.Procedural;

namespace Content.Server._TarkovStation;

/// <summary>Optional alpha test actor, never a substitute for a real network-client test.</summary>
[RegisterComponent]
public sealed partial class TarkovTestBotComponent : Component
{
    public string OwnerUser = "";
    public bool Target;
}
