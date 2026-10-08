// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Configuration;

namespace Content.Shared._TarkovStation;

/// <summary>Configuration shared by the alpha server, interface and automated scenarios.</summary>
[CVarDefs]
public sealed class TarkovCVars
{
    public const string DiscordUrl = "https://discord.gg/jgUJ9Kwa7K";
    public static readonly CVarDef<bool> Enabled = CVarDef.Create("tarkov.enabled", false, CVar.SERVER | CVar.REPLICATED);
    public static readonly CVarDef<int> CycleSeconds = CVarDef.Create("tarkov.cycle_seconds", 36000, CVar.SERVERONLY);
    public static readonly CVarDef<int> QueueSeconds = CVarDef.Create("tarkov.queue_seconds", 45, CVar.SERVER | CVar.REPLICATED);
    public static readonly CVarDef<int> RaidSeconds = CVarDef.Create("tarkov.raid_seconds", 900, CVar.SERVERONLY);
    public static readonly CVarDef<int> ExtractionSeconds = CVarDef.Create("tarkov.extraction_seconds", 8, CVar.SERVER | CVar.REPLICATED);
    // Retained for old configuration compatibility; gameplay always permits only one active raid.
    public static readonly CVarDef<int> MaxRaids = CVarDef.Create("tarkov.max_raids", 1, CVar.SERVERONLY);
    public static readonly CVarDef<int> StartingMoney = CVarDef.Create("tarkov.starting_money", 2500, CVar.SERVERONLY);
    public static readonly CVarDef<string> Database = CVarDef.Create("tarkov.database", "tarkov-alpha.db", CVar.SERVERONLY);
    public static readonly CVarDef<bool> TestBots = CVarDef.Create("tarkov.test_bots", false, CVar.SERVERONLY);
}
