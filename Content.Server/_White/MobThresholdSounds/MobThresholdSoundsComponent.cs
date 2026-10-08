// SPDX-FileCopyrightText: 2025 Space Station 14 Contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Maths.FixedPoint;
using Robust.Shared.Audio;

namespace Content.Server._White.MobThresholdSounds;

[RegisterComponent]
public sealed partial class MobThresholdSoundsComponent : Component
{
    [DataField]
    public SoundSpecifier? DeathSound;

    [DataField]
    public SoundSpecifier? CritSound;

    [DataField]
    public FixedPoint2 CritThreshold = FixedPoint2.New(-50);

    [DataField]
    public bool PlayedCritSound;

    [DataField]
    public bool PlayedDeathSound;
}
