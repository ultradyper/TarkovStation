// SPDX-FileCopyrightText: 2026 Space Station 14 Contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Goobstation.Heretic.Components;

namespace Content.Client._Shitcode.Heretic.SpriteOverlay;

public sealed class EntropicPlumeOverlaySystem : SpriteOverlaySystem<EntropicPlumeAffectedComponent>
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<EntropicPlumeAffectedComponent, AfterAutoHandleStateEvent>((uid, comp, _) =>
            AddOverlay(uid, comp));
    }
}
