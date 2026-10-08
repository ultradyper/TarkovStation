// SPDX-FileCopyrightText: 2026 Ceterai <ceterai@protonmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._Reserve.Chemistry.Components;

[RegisterComponent]
public sealed partial class SolutionHeaterAutoComponent : Component
{
    /// <summary>
    /// How much heat is added per second to the solution.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float HeatPerSecond;

    /// <summary>
    /// Maximum temperature the solution will be heated to.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float MaxHeat;

    /// <summary>
    /// The name of the solution to heat.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public string Solution = string.Empty;
}
