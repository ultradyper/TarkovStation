// SPDX-FileCopyrightText: 2025 Goob Station Contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later


namespace Content.Goobstation.Shared.Body;

[ByRefEvent]
public record struct CheckNeedsAirEvent(
    bool Cancelled);
