// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._TarkovStation.Components;

/// <summary>Safe-zone hooks on hub entities, separate from other forks' component subscriptions.</summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class TarkovHubProtectedComponent : Component;
