// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._TarkovStation.Persistence;

public sealed class TarkovTrade
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string A { get; set; } = "";
    public string B { get; set; } = "";
    public long MoneyA { get; set; }
    public long MoneyB { get; set; }
    public List<string> ItemsA { get; set; } = new();
    public List<string> ItemsB { get; set; } = new();
    public bool AcceptedA { get; set; }
    public bool AcceptedB { get; set; }
    public string Status { get; set; } = "open";

    internal TarkovTrade Copy()
    {
        var copy = (TarkovTrade)MemberwiseClone();
        copy.ItemsA = new List<string>(ItemsA);
        copy.ItemsB = new List<string>(ItemsB);
        return copy;
    }
}
