// SPDX-FileCopyrightText: 2025 Cinkafox <70429757+Cinkafox@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 ReserveBot <211949879+ReserveBot@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._White.Bark;
using Robust.Shared.Audio;

namespace Content.Client._White.Bark;

[RegisterComponent]
public sealed partial class BarkSourceComponent : Component
{
    [DataField]
    public Queue<BarkData> Barks { get; set; } = new();

    [DataField]
    public SoundSpecifier ResolvedSound { get; set; }

    [ViewVariables]
    public BarkData? CurrentBark { get; set; }

    [ViewVariables]
    public float BarkTime { get; set; }
}
