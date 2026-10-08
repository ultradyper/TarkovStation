// SPDX-License-Identifier: AGPL-3.0-or-later
#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
using Content.Server._TarkovStation.Persistence;
using Content.Shared._TarkovStation.Prototypes;
using Content.Shared._TarkovStation;

namespace Content.IntegrationTests.Tests._TarkovStation;

[TestFixture]
public sealed class TarkovLootTest
{
    [Test]
    public void RaidConditionsRotatePersistAndRewardDanger()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tarkov-phase-" + Guid.NewGuid() + ".db");
        try
        {
            using (var repository = new TarkovRepository(path, 100, 36000))
            {
                Assert.That(TarkovRaidConditions.Phase(repository.Read().RaidSequence), Is.EqualTo(TarkovDayPhase.Day));
                repository.Execute("system", "raid-created", "test", d => { d.RaidSequence++; return null; }, 101);
            }
            using (var recovered = new TarkovRepository(path, 102, 36000))
            {
                Assert.That(TarkovRaidConditions.Phase(recovered.Read().RaidSequence), Is.EqualTo(TarkovDayPhase.Evening));
                recovered.Execute("system", "second-raid", "test", d => { d.RaidSequence++; return null; }, 103);
                Assert.That(TarkovRaidConditions.Phase(recovered.Read().RaidSequence), Is.EqualTo(TarkovDayPhase.Night));
                recovered.Execute("system", "third-raid", "test", d => { d.RaidSequence++; return null; }, 104);
                Assert.That(TarkovRaidConditions.Phase(recovered.Read().RaidSequence), Is.EqualTo(TarkovDayPhase.Day));
            }
            Assert.That(TarkovRaidConditions.LootBudget(TarkovDayPhase.Day), Is.LessThan(TarkovRaidConditions.LootBudget(TarkovDayPhase.Evening)));
            Assert.That(TarkovRaidConditions.LootBudget(TarkovDayPhase.Evening), Is.LessThan(TarkovRaidConditions.LootBudget(TarkovDayPhase.Night)));
            Assert.That(TarkovRaidConditions.Guards(TarkovDayPhase.Day, 1), Is.LessThan(TarkovRaidConditions.Guards(TarkovDayPhase.Night, 1)));
            Assert.That(TarkovRaidConditions.Monsters(TarkovDayPhase.Day, 1), Is.LessThan(TarkovRaidConditions.Monsters(TarkovDayPhase.Night, 1)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) System.IO.File.Delete(path + suffix);
        }
    }

    [Test]
    public void SeededRollsRespectBudgetExcludeInvalidWeightsAndAreRepeatable()
    {
        var catalogue = new Dictionary<string, TarkovGoodsPrototype>
        {
            ["cheap"] = new() { Product = "Wrench", Sell = 20 },
            ["rare"] = new() { Product = "WeaponRifleLecter", Sell = 700 },
            ["invalid"] = new() { Product = "Wrench", Sell = -1 },
        };
        var table = new TarkovLootTablePrototype
        {
            Budget = 1000, Rolls = 6,
            Entries = new() { new() { Goods = "cheap", Weight = 10 }, new() { Goods = "rare", Weight = 2 },
                new() { Goods = "invalid", Weight = 100 }, new() { Goods = "missing", Weight = 100 } },
        };
        var sawRare = false;
        for (var seed = 0; seed < 1000; seed++)
        {
            var a = TarkovLootRoller.Roll(table, catalogue, new Random(seed));
            var b = TarkovLootRoller.Roll(table, catalogue, new Random(seed));
            Assert.That(a.Select(g => g.Product.Id), Is.EqualTo(b.Select(g => g.Product.Id)));
            Assert.That(a.Sum(g => g.Sell), Is.LessThanOrEqualTo(table.Budget));
            Assert.That(a.All(g => g.Sell > 0), Is.True);
            Assert.That(a.Count, Is.LessThanOrEqualTo(table.Rolls));
            sawRare |= a.Any(g => g.Sell == 700);
        }
        Assert.That(sawRare, Is.True, "A rare eligible entry must be obtainable");
        table.Budget = 0; Assert.That(TarkovLootRoller.Roll(table, catalogue, new Random(1)), Is.Empty);
    }

    [Test]
    public void DeploymentRejectsWholeRosterBeforeChangingAnyAccount()
    {
        using var repository = new TarkovRepository(":memory:", 100, 36000);
        repository.Execute("system", "create", "test", data =>
        {
            TarkovEconomy.Create(data, "a", "A", "", "", "", 100);
            TarkovEconomy.Create(data, "b", "B", "", "", "", 100);
            data.Accounts["b"].Location = "raid";
            return null;
        }, 100);
        var roster = new List<(string User, string Life, List<TarkovStoredItem> Items)>
        {
            ("a", "life-a", new()), ("b", "life-b", new()),
        };
        Assert.That(repository.Execute("system", "depart", "deploy",
            d => TarkovEconomy.Deploy(d, "raid", roster), 101), Is.EqualTo("tarkov-error-raid"));
        Assert.That(repository.Read().Accounts["a"].Location, Is.EqualTo("hub"));
        repository.Execute("system", "return", "test", d => { d.Accounts["b"].Location = "hub"; return null; }, 102);
        Assert.That(repository.Execute("system", "depart", "deploy",
            d => TarkovEconomy.Deploy(d, "raid", roster), 103), Is.Null);
        Assert.That(repository.Read().Accounts.Values.All(a => a.Location == "raid" && a.Raid == "raid"), Is.True);
        Assert.That(repository.Execute("system", "depart", "deploy",
            d => TarkovEconomy.Deploy(d, "raid", roster), 104), Is.EqualTo("tarkov-error-replay"));
    }

    [Test]
    public void EmptyTradeCannotBeConfirmedAndDoesNotChangeWallets()
    {
        var data = new TarkovData();
        TarkovEconomy.Create(data, "a", "A", "", "", "", 100);
        TarkovEconomy.Create(data, "b", "B", "", "", "", 100);
        TarkovEconomy.StartTrade(data, "a", "b", "t");
        Assert.That(TarkovEconomy.AcceptTrade(data, "a", "t"), Is.EqualTo("tarkov-error-empty-trade"));
        Assert.That(data.Trades["t"].Status, Is.EqualTo("open"));
        Assert.That(data.Accounts.Values.Sum(a => a.Balance), Is.EqualTo(200));
    }
}
