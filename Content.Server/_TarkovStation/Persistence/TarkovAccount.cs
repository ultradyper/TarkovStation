// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._TarkovStation.Persistence;

public sealed class TarkovAccount
{
    public string User { get; set; } = "";
    public string Name { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Faction { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Party { get; set; } = "";
    public List<string> Invites { get; set; } = new();
    public long Balance { get; set; }
    public long Reserved { get; set; }
    public int Kills { get; set; }
    public int Extractions { get; set; }
    public string Location { get; set; } = "hub";
    public string Life { get; set; } = Guid.NewGuid().ToString("N");
    public string Raid { get; set; } = "";
    public long LastEmergencyUtc { get; set; }
    public bool Created { get; set; }
    public bool TestBot { get; set; }
    public string TestOwner { get; set; } = "";
    public bool TestTarget { get; set; }
    public string PendingKit { get; set; } = "";

    internal TarkovAccount Copy()
    {
        var copy = (TarkovAccount)MemberwiseClone();
        copy.Invites = new List<string>(Invites);
        return copy;
    }
}
