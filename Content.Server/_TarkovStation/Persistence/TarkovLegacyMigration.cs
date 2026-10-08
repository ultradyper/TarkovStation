// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Text.Json;

namespace Content.Server._TarkovStation.Persistence;

/// <summary>One-time import of alpha saves. Removed actor metadata is never part of the current account schema.</summary>
internal static class TarkovLegacyMigration
{
    public static bool Upgrade(TarkovData data, string source)
    {
        using var document = JsonDocument.Parse(source);
        var root = document.RootElement;
        var version = root.TryGetProperty("Version", out var storedVersion) ? storedVersion.GetInt32() : 1;
        if (version >= 3) return false;
        var retired = new HashSet<string>();
        if (root.TryGetProperty("Accounts", out var accounts))
        {
            foreach (var account in accounts.EnumerateObject())
                if (account.Value.TryGetProperty("TestBot", out var flag) && flag.ValueKind == JsonValueKind.True)
                    retired.Add(account.Name);
        }
        foreach (var trade in data.Trades.Values.Where(t => retired.Contains(t.A) || retired.Contains(t.B)).ToArray())
        {
            if (trade.Status == "open") TarkovEconomy.CancelTrade(data, trade.A, trade.Id);
            data.Trades.Remove(trade.Id);
        }
        foreach (var contract in data.Contracts.Values.Where(c => retired.Contains(c.Issuer) || retired.Contains(c.Assignee)
            || c.Kind == "kill" && retired.Contains(c.Target)).ToArray())
        {
            if (contract.Status is "open" or "accepted" or "proof")
            {
                var issuer = data.Accounts[contract.Issuer];
                issuer.Reserved -= contract.Reward;
                issuer.Balance += contract.Reward;
            }
            data.Contracts.Remove(contract.Id);
        }
        foreach (var item in data.Items.Values.Where(i => retired.Contains(i.Owner)).ToArray()) data.Items.Remove(item.Id);
        foreach (var user in retired) data.Accounts.Remove(user);
        foreach (var account in data.Accounts.Values)
        {
            account.Invites.RemoveAll(retired.Contains);
            if (retired.Count > 0 && account.Party != "" && data.Accounts.Values.Count(a => a.Party == account.Party) < 2)
                account.Party = "";
        }
        data.Version = 3;
        return true;
    }
}
