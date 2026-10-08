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
    public static Color Ambient(TarkovDayPhase phase) => Color.FromHex(phase switch
    {
        TarkovDayPhase.Evening => "#99745C",
        TarkovDayPhase.Night => "#2B3143",
        _ => "#E6CB8B",
    });

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
