// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;

namespace Content.Server._TarkovStation.Persistence;

/// <summary>Transaction operations for wallets, equipment, contracts and player trades.</summary>
public static class TarkovEconomy
{
    /// <summary>Validate the complete roster before changing any account or carried item.</summary>
    public static string? Deploy(TarkovData data, string raid,
        IReadOnlyList<(string User, string Life, List<TarkovStoredItem> Items)> roster)
    {
        if (raid == "" || roster.Count == 0 || roster.Select(p => p.User).Distinct().Count() != roster.Count)
            return "tarkov-error-raid";
        foreach (var member in roster)
        {
            if (!data.Accounts.TryGetValue(member.User, out var account) || account.Location != "hub"
                || !account.Created || member.Life == ""
                || data.Trades.Values.Any(t => t.Status == "open" && (t.A == member.User || t.B == member.User)))
                return "tarkov-error-raid";
            if (member.Items.Any(i => i.Owner != member.User || i.Location != "raid"))
                return "tarkov-error-item";
        }
        foreach (var member in roster)
        {
            var account = data.Accounts[member.User];
            account.Location = "raid";
            account.Life = member.Life;
            account.Raid = raid;
            foreach (var item in member.Items) data.Items[item.Id] = item;
        }
        return null;
    }

    public static long Wealth(TarkovData data, TarkovAccount user) => user.Balance + user.Reserved
        + data.Items.Values.Where(i => i.Owner == user.User && i.Parent == ""
            && i.Location is "stash" or "hub" or "trade").Sum(i => i.Emergency ? i.ContainedValue : i.Value);

    public static string? Create(TarkovData data, string user, string name, string profile, string faction, string branch, int money)
    {
        if (data.Accounts.TryGetValue(user, out var previous) && previous.Created)
            return "tarkov-error-created";
        data.Accounts[user] = new TarkovAccount
        {
            User = user, Name = name, Profile = profile, Faction = faction, Branch = branch,
            Balance = money, Created = true, PendingKit = branch,
        };
        return null;
    }

    public static string? Debit(TarkovData data, string user, long amount)
    {
        if (amount < 0 || !data.Accounts.TryGetValue(user, out var account))
            return "tarkov-error-amount";
        if (account.Balance < amount)
            return "tarkov-error-money";
        account.Balance -= amount;
        return null;
    }

    public static string? Move(TarkovData data, string user, string id, string from, string to)
    {
        if (!data.Items.TryGetValue(id, out var item) || item.Owner != user || item.Location != from || item.Parent != "")
            return "tarkov-error-item";
        if (to == "stash" && data.Items.Values.Count(i => i.Owner == user && i.Location == "stash" && i.Parent == "") >= 64)
            return "tarkov-error-stash-full";
        item.Location = to;
        foreach (var child in data.Items.Values.Where(i => i.Parent == item.Id)) child.Location = to;
        return null;
    }

    public static string? Sell(TarkovData data, string user, string id)
    {
        if (!data.Items.TryGetValue(id, out var item) || item.Owner != user || item.Location != "stash" || item.Parent != ""
            || item.Emergency || item.Value <= 0)
            return "tarkov-error-item";
        data.Accounts[user].Balance += item.Value;
        RetireTree(data, item.Id);
        return null;
    }

    public static void RetireTree(TarkovData data, string id)
    {
        foreach (var child in data.Items.Values.Where(i => i.Parent == id).ToArray())
            RetireTree(data, child.Id);
        if (data.Items.TryGetValue(id, out var item))
            item.Location = "gone";
    }

    public static string? CreateContract(TarkovData data, TarkovContract contract, long now)
    {
        if (contract.Reward <= 0 || contract.Reward > 1_000_000 || contract.Count is < 1 or > 100
            || contract.EndsUtc <= now || contract.EndsUtc > data.EndsUtc || contract.Text.Length > 500
            || contract.Kind is not ("item" or "kill" or "custom")
            || (contract.Kind == "custom" && contract.Text.Trim().Length < 3)
            || !data.Accounts.TryGetValue(contract.Issuer, out var issuer))
            return "tarkov-error-contract";
        if (data.Contracts.Values.Count(c => c.Issuer == issuer.User && c.Status is "open" or "accepted" or "proof") >= 10)
            return "tarkov-error-contract";
        var error = Debit(data, issuer.User, contract.Reward);
        if (error != null)
            return error;
        issuer.Reserved += contract.Reward;
        data.Contracts.Add(contract.Id, contract);
        return null;
    }

    public static string? AcceptContract(TarkovData data, string user, string id, long now)
    {
        if (!data.Contracts.TryGetValue(id, out var contract) || contract.Status != "open" || contract.Issuer == user
            || contract.EndsUtc <= now || !data.Accounts.ContainsKey(user) || (contract.Kind == "kill" && contract.Target == user))
            return "tarkov-error-contract";
        contract.Assignee = user;
        contract.Status = "accepted";
        return null;
    }

    public static string? CancelContract(TarkovData data, string user, string id)
    {
        if (!data.Contracts.TryGetValue(id, out var contract) || contract.Issuer != user || contract.Status != "open")
            return "tarkov-error-contract";
        Refund(data, contract, "cancelled");
        return null;
    }

    public static void Expire(TarkovData data, long now)
    {
        foreach (var contract in data.Contracts.Values.Where(c => c.EndsUtc <= now && c.Status is "open" or "accepted" or "proof"))
            Refund(data, contract, "expired");
    }

    private static void Refund(TarkovData data, TarkovContract contract, string status)
    {
        var issuer = data.Accounts[contract.Issuer];
        issuer.Reserved -= contract.Reward;
        issuer.Balance += contract.Reward;
        contract.Status = status;
    }

    public static void Pay(TarkovData data, TarkovContract contract)
    {
        if (contract.Status is not ("accepted" or "proof") || contract.Assignee == "")
            return;
        data.Accounts[contract.Issuer].Reserved -= contract.Reward;
        data.Accounts[contract.Assignee].Balance += contract.Reward;
        contract.Status = "paid";
    }

    public static void RecordKill(TarkovData data, string killer, string victim, string raid, string life, long now)
    {
        if (killer == victim || !data.Accounts.TryGetValue(killer, out var account))
            return;
        account.Kills++;
        if (account.Raid != raid || account.Life != life || account.Location != "raid") return;
        foreach (var contract in data.Contracts.Values.Where(c => c.Kind == "kill" && c.Target == victim
            && c.Assignee == killer && c.Status == "accepted" && c.EndsUtc > now))
        {
            contract.Status = "proof";
            contract.ProofLife = life;
            contract.ProofRaid = raid;
        }
    }

    public static string? Extract(TarkovData data, string user, string raid, string life, long now)
    {
        if (!data.Accounts.TryGetValue(user, out var account) || account.Location != "raid" || account.Raid != raid || account.Life != life)
            return "tarkov-error-raid";
        foreach (var contract in data.Contracts.Values.Where(c => c.Kind == "kill" && c.Status == "proof"
            && c.Assignee == user && c.ProofRaid == raid && c.ProofLife == life && c.EndsUtc > now))
            Pay(data, contract);
        account.Location = "hub";
        account.Raid = "";
        account.Extractions++;
        return null;
    }

    public static void LoseLife(TarkovData data, string user)
    {
        if (!data.Accounts.TryGetValue(user, out var account))
            return;
        foreach (var item in data.Items.Values.Where(i => i.Owner == user && i.Location == "raid"))
            item.Location = "lost";
        foreach (var contract in data.Contracts.Values.Where(c => c.Assignee == user && c.Status == "proof" && c.ProofLife == account.Life))
        {
            contract.Status = "accepted";
            contract.ProofLife = "";
            contract.ProofRaid = "";
        }
        account.Location = "hub";
        account.Raid = "";
        account.Life = Guid.NewGuid().ToString("N");
    }

    public static string? StartTrade(TarkovData data, string a, string b, string id)
    {
        if (a == b || !data.Accounts.TryGetValue(a, out var aa) || !data.Accounts.TryGetValue(b, out var bb)
            || aa.Location != "hub" || bb.Location != "hub" || data.Trades.Values.Any(t => t.Status == "open"
                && (t.A == a || t.B == a || t.A == b || t.B == b)))
            return "tarkov-error-trade";
        data.Trades[id] = new TarkovTrade { Id = id, A = a, B = b };
        return null;
    }

    public static string? TradeMoney(TarkovData data, string user, string id, long amount)
    {
        if (amount < 0 || !data.Trades.TryGetValue(id, out var trade) || trade.Status != "open" || (user != trade.A && user != trade.B))
            return "tarkov-error-trade";
        var old = user == trade.A ? trade.MoneyA : trade.MoneyB;
        var account = data.Accounts[user];
        if (account.Balance + old < amount)
            return "tarkov-error-money";
        account.Balance += old - amount;
        account.Reserved += amount - old;
        if (user == trade.A) trade.MoneyA = amount; else trade.MoneyB = amount;
        trade.AcceptedA = trade.AcceptedB = false;
        return null;
    }

    public static string? TradeItem(TarkovData data, string user, string id, string itemId, bool add)
    {
        if (!data.Trades.TryGetValue(id, out var trade) || trade.Status != "open" || (user != trade.A && user != trade.B))
            return "tarkov-error-trade";
        var error = Move(data, user, itemId, add ? "stash" : "trade", add ? "trade" : "stash");
        if (error != null)
            return error;
        var list = user == trade.A ? trade.ItemsA : trade.ItemsB;
        if (add) list.Add(itemId); else list.Remove(itemId);
        trade.AcceptedA = trade.AcceptedB = false;
        return null;
    }

    public static string? AcceptTrade(TarkovData data, string user, string id)
    {
        if (!data.Trades.TryGetValue(id, out var trade) || trade.Status != "open" || (user != trade.A && user != trade.B))
            return "tarkov-error-trade";
        if (trade.MoneyA == 0 && trade.MoneyB == 0 && trade.ItemsA.Count == 0 && trade.ItemsB.Count == 0)
            return "tarkov-error-empty-trade";
        if (user == trade.A) trade.AcceptedA = true; else trade.AcceptedB = true;
        if (!trade.AcceptedA || !trade.AcceptedB)
            return null;
        foreach (var (ids, owner, next) in new[] { (trade.ItemsA, trade.A, trade.B), (trade.ItemsB, trade.B, trade.A) })
        {
            if (ids.Any(i => !data.Items.TryGetValue(i, out var item) || item.Owner != owner || item.Location != "trade"))
                return "tarkov-error-item";
            foreach (var idItem in ids)
                TransferTree(data, idItem, next);
        }
        data.Accounts[trade.A].Reserved -= trade.MoneyA;
        data.Accounts[trade.B].Reserved -= trade.MoneyB;
        data.Accounts[trade.A].Balance += trade.MoneyB;
        data.Accounts[trade.B].Balance += trade.MoneyA;
        trade.Status = "done";
        return null;
    }

    public static void TransferTree(TarkovData data, string id, string next)
    {
        var item = data.Items[id];
        item.Owner = next;
        item.Location = "stash";
        foreach (var child in data.Items.Values.Where(i => i.Parent == id).ToArray())
            TransferTree(data, child.Id, next);
    }

    public static string? CancelTrade(TarkovData data, string user, string id)
    {
        if (!data.Trades.TryGetValue(id, out var trade) || trade.Status != "open" || (user != trade.A && user != trade.B))
            return "tarkov-error-trade";
        foreach (var (owner, money, items) in new[] { (trade.A, trade.MoneyA, trade.ItemsA), (trade.B, trade.MoneyB, trade.ItemsB) })
        {
            data.Accounts[owner].Reserved -= money;
            data.Accounts[owner].Balance += money;
            foreach (var item in items)
            {
                data.Items[item].Location = "stash";
                foreach (var child in data.Items.Values.Where(i => i.Parent == item)) child.Location = "stash";
            }
        }
        trade.Status = "cancelled";
        return null;
    }

    public static void RollCycle(TarkovData data, long now, int duration)
    {
        foreach (var trade in data.Trades.Values.Where(t => t.Status == "open").ToArray())
            CancelTrade(data, trade.A, trade.Id);
        foreach (var contract in data.Contracts.Values.Where(c => c.Status is "open" or "accepted" or "proof"))
            Refund(data, contract, "expired");
        data.Results = data.Accounts.Values.Where(a => a.Created).Select(a => new TarkovResult
        {
            Cycle = data.Cycle, Name = a.Name, Faction = a.Faction, Wealth = Wealth(data, a), Kills = a.Kills, Extractions = a.Extractions,
        }).OrderByDescending(r => r.Wealth).Take(100).ToList();
        data.Accounts.Clear();
        data.Items.Clear();
        data.Contracts.Clear();
        data.Trades.Clear();
        data.Cycle = Guid.NewGuid().ToString("N");
        data.RaidSequence = 0;
        data.EndsUtc = now + duration;
    }
}
