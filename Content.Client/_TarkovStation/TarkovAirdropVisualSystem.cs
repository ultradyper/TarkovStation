// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._TarkovStation.Components;
using Robust.Client.GameObjects;
using Robust.Shared.Timing;

namespace Content.Client._TarkovStation;

public sealed class TarkovAirdropVisualSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;

    public override void FrameUpdate(float frameTime)
    {
        var query = EntityQueryEnumerator<TarkovAirdropVisualComponent, SpriteComponent>();
        while (query.MoveNext(out _, out var drop, out var sprite))
        {
            var progress = Math.Clamp((float)(drop.LandsAt - _timing.CurTime).TotalSeconds / TarkovAirdropVisualComponent.FallSeconds, 0, 1);
            sprite.Offset = new Vector2(0, 9 * progress);
        }
    }
}
