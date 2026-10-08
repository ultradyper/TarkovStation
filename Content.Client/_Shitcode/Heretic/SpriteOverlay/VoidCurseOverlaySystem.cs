// SPDX-FileCopyrightText: 2026 Space Station 14 Contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Goobstation.Heretic.Components;
using Robust.Client.GameObjects;

namespace Content.Client._Shitcode.Heretic.SpriteOverlay;

public sealed class VoidCurseOverlaySystem : SpriteOverlaySystem<VoidCurseComponent>
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<VoidCurseComponent, AfterAutoHandleStateEvent>((uid, comp, _) =>
            AddOverlay(uid, comp));
    }

    protected override void UpdateOverlayLayer(Entity<SpriteComponent> ent,
        VoidCurseComponent comp,
        int layer,
        EntityUid? source = null)
    {
        base.UpdateOverlayLayer(ent, comp, layer, source);
        var state = comp.Stacks >= comp.MaxLifetime ? comp.OverlayStateMax : comp.OverlayStateNormal;
        Sprite.LayerSetRsiState(ent.AsNullable(), layer, state);
    }
}
