// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Shared._TarkovStation;

/// <summary>One fixed set of conditions for the entire raid, including later arrivals.</summary>
public enum TarkovDayPhase : byte
{
    Day,
    Evening,
    Night,
}

/// <summary>Initial balance values. Sequence advances only after successful creation, never on late entry.</summary>
public static class TarkovRaidConditions
{
    public static TarkovDayPhase Phase(long sequence) => (TarkovDayPhase)(Math.Max(0, sequence) % 3);

    public static string Key(TarkovDayPhase phase) => phase switch
    {
        TarkovDayPhase.Evening => "evening",
        TarkovDayPhase.Night => "night",
        _ => "day",
    };

    // Native planetary lighting colors; keep the selected expedition phase stable for 15 minutes.
    public static Color Ambient(TarkovDayPhase phase) => phase switch
    {
        // MapLight uses linear light. Daylight should read as daylight even on dark
        // dirt/grass textures; the old yellow tint made daytime resemble dusk.
        TarkovDayPhase.Day => new Color(1.35f, 1.32f, 1.25f),
        TarkovDayPhase.Evening => new Color(1.2f, 1.06f, 0.88f),
        _ => Color.FromHex("#2B3143"),
    };

    public static float LootBudget(TarkovDayPhase phase) => phase switch
    {
        TarkovDayPhase.Evening => 1f,
        TarkovDayPhase.Night => 1.6f,
        _ => 0.65f,
    };

    public static int Guards(TarkovDayPhase phase, int participants)
        => Math.Clamp(2 + participants / 3 + (int)phase * 2, 2, 14);

    public static int Monsters(TarkovDayPhase phase, int participants)
        => Math.Clamp(3 + participants / 2 + (int)phase * 3, 3, 20);
}
