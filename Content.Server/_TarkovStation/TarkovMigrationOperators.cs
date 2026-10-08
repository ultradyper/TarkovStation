// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;

namespace Content.Server._TarkovStation;

public sealed partial class TarkovMigrationTargetOperator : HTNOperator
{
    [Dependency] private IEntityManager _entities = default!;
    public override Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard, CancellationToken cancelToken)
    {
        var valid = _entities.System<TarkovSystem>().MigrationDestination(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner), out var target);
        return Task.FromResult<(bool, Dictionary<string, object>?)>((valid,
            valid ? new Dictionary<string, object> { ["TarkovMigrationTarget"] = target, ["TarkovMigrationRange"] = 1.2f } : null));
    }
}

public sealed partial class TarkovMigrationAdvanceOperator : HTNOperator
{
    [Dependency] private IEntityManager _entities = default!;
    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        _entities.System<TarkovSystem>().AdvanceMigration(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner));
        return HTNOperatorStatus.Finished;
    }
}
