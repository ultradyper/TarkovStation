// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Robust.Shared.Map;

namespace Content.Server._TarkovStation;

/// <summary>Native HTN priority before ranged combat: use real spare magazines or switch to the carried knife.</summary>
public sealed partial class TarkovMaintainWeaponOperator : HTNOperator
{
    [Dependency] private IEntityManager _entities = default!;

    public override Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard, CancellationToken cancelToken)
        => Task.FromResult<(bool, Dictionary<string, object>?)>((_entities.System<TarkovRaiderSystem>()
            .NeedsWeaponService(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner)), null));

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
        => _entities.System<TarkovRaiderSystem>().MaintainWeapon(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner));
}

/// <summary>Select an actual visible crate or loose item; movement is delegated to the stock MoveToOperator.</summary>
public sealed partial class TarkovFindLootOperator : HTNOperator
{
    [Dependency] private IEntityManager _entities = default!;

    public override Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard, CancellationToken cancelToken)
    {
        var target = _entities.System<TarkovRaiderSystem>().FindLoot(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner));
        return Task.FromResult<(bool, Dictionary<string, object>?)>(target == null ? (false, null)
            : (true, new Dictionary<string, object>
            {
                ["TarkovLootTarget"] = target.Value,
                ["TarkovLootCoordinates"] = new EntityCoordinates(target.Value, Vector2.Zero),
            }));
    }

    public override void TaskShutdown(NPCBlackboard blackboard, HTNOperatorStatus status)
    {
        if (status == HTNOperatorStatus.Failed && blackboard.TryGetValue<EntityUid>("TarkovLootTarget", out var target, _entities))
            _entities.System<TarkovRaiderSystem>().IgnoreLoot(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner), target);
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        // Reserve the attempt before pathfinding: a blocked crate must not trap repeated plans forever.
        if (blackboard.TryGetValue<EntityUid>("TarkovLootTarget", out var target, _entities))
            _entities.System<TarkovRaiderSystem>().IgnoreLoot(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner), target);
    }
}

/// <summary>Open and loot nearby storage through native APIs, preserving each item's identity and ammunition.</summary>
public sealed partial class TarkovCollectLootOperator : HTNOperator
{
    [Dependency] private IEntityManager _entities = default!;

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        if (!blackboard.TryGetValue<EntityUid>("TarkovLootTarget", out var target, _entities)) return HTNOperatorStatus.Failed;
        return _entities.System<TarkovRaiderSystem>().CollectLoot(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner), target)
            ? HTNOperatorStatus.Finished : HTNOperatorStatus.Failed;
    }
}
