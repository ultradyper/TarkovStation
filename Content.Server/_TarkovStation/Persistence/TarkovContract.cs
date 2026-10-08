// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._TarkovStation.Persistence;

public sealed class TarkovContract
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Issuer { get; set; } = "";
    public string Assignee { get; set; } = "";
    public string Kind { get; set; } = "item";
    public string Target { get; set; } = "";
    public string Text { get; set; } = "";
    public int Count { get; set; } = 1;
    public long Reward { get; set; }
    public long EndsUtc { get; set; }
    public string Status { get; set; } = "open";
    public string ProofLife { get; set; } = "";
    public string ProofRaid { get; set; } = "";

    internal TarkovContract Copy() => (TarkovContract)MemberwiseClone();
}
