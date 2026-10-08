// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._TarkovStation.Persistence;

public sealed class TarkovResult
{
    public string Cycle { get; set; } = "";
    public string Name { get; set; } = "";
    public string Faction { get; set; } = "";
    public long Wealth { get; set; }
    public int Kills { get; set; }
    public int Extractions { get; set; }

    internal TarkovResult Copy() => (TarkovResult)MemberwiseClone();
}
