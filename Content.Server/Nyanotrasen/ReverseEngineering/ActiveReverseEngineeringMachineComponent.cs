// SPDX-FileCopyrightText: 2026 Space Station 14 Contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization.TypeSerializers.Implementations;

namespace Content.Server.Nyanotrasen.ReverseEngineering;

[RegisterComponent]
public sealed partial class ActiveReverseEngineeringMachineComponent : Component
{
    /// <summary>
    /// When did the scanning start?
    /// </summary>
    [DataField("startTime", customTypeSerializer: typeof(TimespanSerializer))]
    public TimeSpan StartTime;

    /// <summary>
    /// What is being scanned?
    /// </summary>
    [ViewVariables]
    public EntityUid Item;
}
