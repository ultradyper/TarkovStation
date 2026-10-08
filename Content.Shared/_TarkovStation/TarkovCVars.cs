// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Configuration;

namespace Content.Shared._TarkovStation;

/// <summary>Configuration shared by the server and player interface.</summary>
[CVarDefs]
public sealed class TarkovCVars
{
    public const string DiscordUrl = "https://discord.gg/jgUJ9Kwa7K";
    public static readonly CVarDef<bool> Enabled = CVarDef.Create("tarkov.enabled", false, CVar.SERVER | CVar.REPLICATED);
    public static readonly CVarDef<int> CycleSeconds = CVarDef.Create("tarkov.cycle_seconds", 36000, CVar.SERVERONLY);
    public static readonly CVarDef<int> QueueSeconds = CVarDef.Create("tarkov.queue_seconds", 45, CVar.SERVER | CVar.REPLICATED);
    public static readonly CVarDef<int> RaidSeconds = CVarDef.Create("tarkov.raid_seconds", 900, CVar.SERVERONLY);
    public static readonly CVarDef<int> ExtractionSeconds = CVarDef.Create("tarkov.extraction_seconds", 8, CVar.SERVER | CVar.REPLICATED);
    public static readonly CVarDef<int> StartingMoney = CVarDef.Create("tarkov.starting_money", 2500, CVar.SERVERONLY);
    public static readonly CVarDef<string> Database = CVarDef.Create("tarkov.database", "tarkovstation.db", CVar.SERVERONLY);
    public static readonly CVarDef<float> HungerRate = CVarDef.Create("tarkov.hunger_rate", 0.0125f, CVar.SERVERONLY);
    public static readonly CVarDef<float> ThirstRate = CVarDef.Create("tarkov.thirst_rate", 0.0375f, CVar.SERVERONLY);
}
