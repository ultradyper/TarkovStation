// SPDX-License-Identifier: AGPL-3.0-or-later
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._TarkovStation.Persistence;

namespace Content.IntegrationTests.Tests._TarkovStation;

[TestFixture]
public sealed class TarkovEconomyTest
{
    private static TarkovData Players()
    {
        var data = new TarkovData { EndsUtc = 10000 };
        foreach (var user in new[] { "a", "b", "c" })
            Assert.That(TarkovEconomy.Create(data, user, user, "profile", "Exiles", "Medic", 1000), Is.Null);
        return data;
    }

    [Test]
    public void InvalidMoneyAndRepeatedCharacterCannotMintMoney()
    {
        var data = Players();
        Assert.That(TarkovEconomy.Debit(data, "a", -1), Is.Not.Null);
        Assert.That(TarkovEconomy.Debit(data, "a", 1001), Is.Not.Null);
        Assert.That(TarkovEconomy.Create(data, "a", "new", "", "", "", 100000), Is.Not.Null);
        Assert.That(data.Accounts["a"].Balance, Is.EqualTo(1000));
    }

    [Test]
    public void EscrowChangesInvalidateBothConfirmationsAndCancelRefunds()
    {
        var d = Players();
        d.Items["bag"] = new TarkovStoredItem { Id = "bag", Owner = "a", Value = 50 };
        d.Items["mag"] = new TarkovStoredItem { Id = "mag", Owner = "a", Parent = "bag" };
        Assert.That(TarkovEconomy.StartTrade(d, "a", "b", "t"), Is.Null);
        Assert.That(TarkovEconomy.TradeMoney(d, "a", "t", 200), Is.Null);
        Assert.That(TarkovEconomy.TradeItem(d, "a", "t", "bag", true), Is.Null);
        Assert.That(TarkovEconomy.AcceptTrade(d, "a", "t"), Is.Null);
        Assert.That(TarkovEconomy.TradeMoney(d, "b", "t", 10), Is.Null);
        Assert.That(d.Trades["t"].AcceptedA, Is.False);
        Assert.That(TarkovEconomy.Sell(d, "a", "bag"), Is.Not.Null);
        Assert.That(TarkovEconomy.CancelTrade(d, "b", "t"), Is.Null);
        Assert.That(d.Accounts.Values.Sum(a => a.Balance), Is.EqualTo(3000));
        Assert.That(d.Accounts.Values.Sum(a => a.Reserved), Is.Zero);
        Assert.That(d.Items["bag"].Location, Is.EqualTo("stash"));
        Assert.That(TarkovEconomy.CancelTrade(d, "a", "t"), Is.Not.Null);
    }

    [Test]
    public void TradeTransfersNestedOwnershipAndConservesCurrency()
    {
        var d = Players();
        d.Items["bag"] = new TarkovStoredItem { Id = "bag", Owner = "a", Value = 50 };
        d.Items["mag"] = new TarkovStoredItem { Id = "mag", Owner = "a", Parent = "bag" };
        TarkovEconomy.StartTrade(d, "a", "b", "t");
        TarkovEconomy.TradeMoney(d, "b", "t", 200);
        TarkovEconomy.TradeItem(d, "a", "t", "bag", true);
        TarkovEconomy.AcceptTrade(d, "a", "t");
        Assert.That(TarkovEconomy.AcceptTrade(d, "b", "t"), Is.Null);
        Assert.That(d.Items.Values.All(i => i.Owner == "b"), Is.True);
        Assert.That(d.Accounts["a"].Balance, Is.EqualTo(1200));
        Assert.That(d.Accounts["b"].Balance, Is.EqualTo(800));
        Assert.That(d.Accounts.Values.Sum(a => a.Reserved), Is.Zero);
        Assert.That(TarkovEconomy.AcceptTrade(d, "a", "t"), Is.Not.Null);
    }

    [Test]
    public void BountyRequiresLivingExtractionInTheSameLifeAndRaid()
    {
        var d = Players();
        var c = new TarkovContract { Id = "c", Issuer = "a", Kind = "kill", Target = "c", Reward = 500, EndsUtc = 500 };
        Assert.That(TarkovEconomy.CreateContract(d, c, 1), Is.Null);
        Assert.That(TarkovEconomy.AcceptContract(d, "b", "c", 2), Is.Null);
        var b = d.Accounts["b"];
        b.Location = "raid"; b.Raid = "r"; b.Life = "l";
        TarkovEconomy.RecordKill(d, "b", "c", "r", "l", 3);
        Assert.That(c.Status, Is.EqualTo("proof"));
        Assert.That(b.Balance, Is.EqualTo(1000));
        Assert.That(TarkovEconomy.Extract(d, "b", "other", "l", 4), Is.Not.Null);
        TarkovEconomy.LoseLife(d, "b");
        Assert.That(c.Status, Is.EqualTo("accepted"));
        b.Location = "raid"; b.Raid = "r2";
        Assert.That(TarkovEconomy.Extract(d, "b", "r2", b.Life, 5), Is.Null);
        Assert.That(b.Balance, Is.EqualTo(1000));
        b.Location = "raid"; b.Raid = "r3";
        TarkovEconomy.RecordKill(d, "b", "c", "r3", b.Life, 6);
        Assert.That(TarkovEconomy.Extract(d, "b", "r3", b.Life, 7), Is.Null);
        Assert.That(b.Balance, Is.EqualTo(1500));
        Assert.That(d.Accounts["a"].Reserved, Is.Zero);
        Assert.That(TarkovEconomy.Extract(d, "b", "r3", b.Life, 8), Is.Not.Null);
    }

    [Test]
    public void DeathKeepsStashAndWalletButRetiresAllRaidGear()
    {
        var d = Players();
        d.Accounts["a"].Location = "raid";
        d.Items["safe"] = new TarkovStoredItem { Id = "safe", Owner = "a", Value = 100 };
        d.Items["gun"] = new TarkovStoredItem { Id = "gun", Owner = "a", Location = "raid", Value = 200 };
        d.Items["mag"] = new TarkovStoredItem { Id = "mag", Owner = "a", Location = "raid", Parent = "gun" };
        TarkovEconomy.LoseLife(d, "a");
        Assert.That(d.Items["safe"].Location, Is.EqualTo("stash"));
        Assert.That(d.Items["gun"].Location, Is.EqualTo("lost"));
        Assert.That(d.Items["mag"].Location, Is.EqualTo("lost"));
        Assert.That(d.Accounts["a"].Balance, Is.EqualTo(1000));
    }

    [Test]
    public void WipeSettlesEscrowKeepsResultsAndExcludesBotsAndLoanValue()
    {
        var d = Players();
        d.Accounts["c"].TestBot = true;
        d.Items["loan"] = new TarkovStoredItem { Id = "loan", Owner = "a", Value = 9999, Emergency = true };
        TarkovEconomy.StartTrade(d, "a", "b", "t");
        TarkovEconomy.TradeMoney(d, "a", "t", 200);
        TarkovEconomy.CreateContract(d, new TarkovContract { Issuer = "b", Reward = 100, EndsUtc = 50 }, 1);
        var cycle = d.Cycle;
        TarkovEconomy.RollCycle(d, 51, 36000);
        Assert.That(d.Results.Count, Is.EqualTo(2));
        Assert.That(d.Results.All(r => r.Wealth == 1000 && r.Cycle == cycle), Is.True);
        Assert.That(d.Accounts, Is.Empty);
        Assert.That(d.Items, Is.Empty);
        Assert.That(d.Cycle, Is.Not.EqualTo(cycle));
    }

    [Test]
    public void LoanContainerDoesNotHideOwnedLootOrAddLoanValue()
    {
        var data = Players();
        data.Items["loan-bag"] = new TarkovStoredItem
        {
            Id = "loan-bag", Owner = "a", Emergency = true, Value = 9999, ContainedValue = 2200,
        };
        data.Items["trophy"] = new TarkovStoredItem { Id = "trophy", Owner = "a", Parent = "loan-bag", Value = 2200 };
        Assert.That(TarkovEconomy.Wealth(data, data.Accounts["a"]), Is.EqualTo(3200));
        Assert.That(TarkovEconomy.Sell(data, "a", "loan-bag"), Is.Not.Null);
        Assert.That(data.Accounts["a"].Balance, Is.EqualTo(1000));
    }

    [Test]
    public void ContractExpiryRefundsOnceAndManualPaymentSettlesOnce()
    {
        var data = Players();
        var expiring = new TarkovContract { Id = "expiry", Issuer = "a", Reward = 200, EndsUtc = 10 };
        Assert.That(TarkovEconomy.CreateContract(data, expiring, 1), Is.Null);
        Assert.That(TarkovEconomy.AcceptContract(data, "b", expiring.Id, 2), Is.Null);
        TarkovEconomy.Expire(data, 10);
        TarkovEconomy.Expire(data, 11);
        Assert.That(data.Accounts["a"].Balance, Is.EqualTo(1000));
        Assert.That(data.Accounts["a"].Reserved, Is.Zero);
        Assert.That(expiring.Status, Is.EqualTo("expired"));
        var custom = new TarkovContract { Id = "custom", Issuer = "a", Kind = "custom", Text = "Помощь с грузом", Reward = 150, EndsUtc = 100 };
        Assert.That(TarkovEconomy.CreateContract(data, custom, 12), Is.Null);
        Assert.That(TarkovEconomy.AcceptContract(data, "b", custom.Id, 13), Is.Null);
        TarkovEconomy.Pay(data, custom); TarkovEconomy.Pay(data, custom);
        Assert.That(data.Accounts["b"].Balance, Is.EqualTo(1150));
        Assert.That(data.Accounts["a"].Balance, Is.EqualTo(850));
        Assert.That(data.Accounts.Values.Sum(a => a.Reserved), Is.Zero);
        Assert.That(TarkovEconomy.CreateContract(data, new TarkovContract
            { Issuer = "a", Kind = "custom", Text = " ", Reward = 1, EndsUtc = 100 }, 14), Is.Not.Null);
    }

    [Test]
    public void SqliteReplayRollbackConcurrentDebitAndRestart()
    {
        // The native provider is a private runtime dependency of the database project.
        System.Reflection.Assembly.Load("SQLitePCLRaw.batteries_v2").GetType("SQLitePCL.Batteries_V2")!
            .GetMethod("Init")!.Invoke(null, null);
        var path = Path.Combine(Path.GetTempPath(), "tarkov-test-" + Guid.NewGuid() + ".db");
        try
        {
            using (var repo = new TarkovRepository(path, 1, 36000))
            {
                Assert.That(repo.Execute("a", "create", "test", d => TarkovEconomy.Create(d, "a", "A", "", "", "Medic", 1000), 1), Is.Null);
                Assert.That(repo.Execute("a", "pay", "test", d => TarkovEconomy.Debit(d, "a", 100), 2), Is.Null);
                Assert.That(repo.Execute("a", "pay", "test", d => TarkovEconomy.Debit(d, "a", 100), 2), Is.EqualTo("tarkov-error-replay"));
                Assert.That(repo.Execute("a", "reject", "test", d => { d.Accounts["a"].Balance = 0; return "rejected"; }, 3), Is.EqualTo("rejected"));
                Parallel.For(0, 20, i => repo.Execute("a", "parallel-" + i, "test", d => TarkovEconomy.Debit(d, "a", 100), 4));
                Assert.That(repo.Read().Accounts["a"].Balance, Is.Zero);
            }
            using var reopened = new TarkovRepository(path, 100, 36000);
            Assert.That(reopened.Read().Accounts["a"].Balance, Is.Zero);
            Assert.That(reopened.Execute("a", "pay", "test", d => null, 100), Is.EqualTo("tarkov-error-replay"));
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }
}
