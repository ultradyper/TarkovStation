// based on https://github.com/space-wizards/space-station-14/pull/34600
using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Reserve.Botany.PlantAnalyzer;

[Serializable, NetSerializable]
public sealed partial class PlantAnalyzerDoAfterEvent : SimpleDoAfterEvent
{
}

