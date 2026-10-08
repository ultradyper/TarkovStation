// SPDX-License-Identifier: AGPL-3.0-or-later
#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Content.Server._TarkovStation.Persistence;
using Microsoft.Data.Sqlite;

namespace Content.IntegrationTests.Tests._TarkovStation;

[TestFixture]
public sealed class TarkovSnapshotRepositoryTest
{
    [OneTimeSetUp]
    public void NativeProvider() => System.Reflection.Assembly.Load("SQLitePCLRaw.batteries_v2")
        .GetType("SQLitePCL.Batteries_V2")!.GetMethod("Init")!.Invoke(null, null);

    [Test]
    public void LegacyBlobsMigrateWithoutChangingFundsIdsOrCycleAndSurviveReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), "tarkov-legacy-" + Guid.NewGuid() + ".db");
        try
        {
            var legacy = new TarkovData { Version = 1, Cycle = "legacy-cycle", EndsUtc = 99999 };
            TarkovEconomy.Create(legacy, "a", "Survivor", "profile", "Exiles", "Medic", 1234);
            var snapshot = new string('x', 500000);
            legacy.Items["bag"] = new TarkovStoredItem { Id = "bag", Owner = "a", Snapshot = snapshot, Value = 77 };
            using (var connection = new SqliteConnection("Data Source=" + path))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE tarkov_state(id INTEGER PRIMARY KEY,payload TEXT); INSERT INTO tarkov_state VALUES(1,$json)";
                command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(legacy)); command.ExecuteNonQuery();
            }
            using (var repo = new TarkovRepository(path, 10, 36000))
            {
                var state = repo.Read();
                Assert.That(state.Cycle, Is.EqualTo("legacy-cycle")); Assert.That(state.Accounts["a"].Balance, Is.EqualTo(1234));
                Assert.That(state.Items["bag"].Snapshot, Is.EqualTo(snapshot));
                state.Accounts["a"].Invites.Add("forged"); state.Items["bag"].Owner = "forged";
                Assert.That(repo.Read().Accounts["a"].Invites, Is.Empty);
                Assert.That(repo.Read().Items["bag"].Owner, Is.EqualTo("a"));
                Assert.That(repo.Execute("a", "reject", "test", d => { d.Items["bag"].Snapshot = "changed"; return "no"; }, 11), Is.EqualTo("no"));
            }
            using (var connection = new SqliteConnection("Data Source=" + path))
            {
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT length(payload) FROM tarkov_state";
                Assert.That(Convert.ToInt64(command.ExecuteScalar()), Is.LessThan(5000), "Mutable metadata must not duplicate the snapshot blob");
                command.CommandText = "SELECT length(snapshot) FROM tarkov_item_snapshots WHERE id='bag'";
                Assert.That(Convert.ToInt64(command.ExecuteScalar()), Is.EqualTo(snapshot.Length));
            }
            using (var repo = new TarkovRepository(path, 20, 36000))
            {
                Assert.That(repo.Read().Items["bag"].Snapshot, Is.EqualTo(snapshot));
                Assert.That(repo.Execute("a", "update", "test", d => { d.Items["bag"].Snapshot = "new-state"; return null; }, 21), Is.Null);
            }
            using (var repo = new TarkovRepository(path, 22, 36000))
            {
                Assert.That(repo.Read().Items["bag"].Snapshot, Is.EqualTo("new-state"));
                Assert.That(repo.Execute("system", "wipe", "cycle", d => { TarkovEconomy.RollCycle(d, 23, 36000); return null; }, 23), Is.Null);
            }
            using var check = new SqliteConnection("Data Source=" + path); check.Open(); using var count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM tarkov_item_snapshots"; Assert.That(Convert.ToInt64(count.ExecuteScalar()), Is.Zero);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Test]
    public void ThirtyTwoFullStashesKeepPollingAllocationsBounded()
    {
        using var repo = new TarkovRepository(":memory:", 1, 36000);
        var snapshot = new string('s', 64000);
        Assert.That(repo.Execute("system", "seed", "test", data =>
        {
            for (var player = 0; player < 32; player++)
            {
                var user = "player-" + player;
                TarkovEconomy.Create(data, user, user, "profile", "Exiles", "Medic", 2500);
                for (var item = 0; item < 64; item++)
                {
                    var id = user + "-" + item;
                    data.Items[id] = new TarkovStoredItem { Id = id, Owner = user, Snapshot = snapshot };
                }
            }
            return null;
        }, 1), Is.Null);
        _ = repo.Read();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            var view = repo.Read();
            Assert.That(view.Items.Count, Is.EqualTo(2048));
            Assert.That(ReferenceEquals(view.Items["player-0-0"].Snapshot, snapshot), Is.True);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        watch.Stop();
        TestContext.WriteLine($"32 accounts / 2048 roots / 131 MB snapshots: 100 polls = {watch.ElapsedMilliseconds} ms, {allocated} allocated bytes");
        Assert.That(allocated, Is.LessThan(96000000), "Polling full stashes must not clone 13 GB of snapshot text");
    }

    [Test]
    public void LargeStashReadDoesNotAllocateCopiesOfImmutableSnapshotText()
    {
        using var repo = new TarkovRepository(":memory:", 1, 36000);
        var snapshot = new string('s', 250000);
        repo.Execute("a", "seed", "test", d =>
        {
            TarkovEconomy.Create(d, "a", "A", "profile", "Exiles", "Medic", 1000);
            for (var i = 0; i < 64; i++) d.Items[i.ToString()] = new TarkovStoredItem { Id = i.ToString(), Owner = "a", Snapshot = snapshot };
            return null;
        }, 1);
        _ = repo.Read();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            var view = repo.Read(); Assert.That(ReferenceEquals(view.Items["0"].Snapshot, snapshot), Is.True);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.That(allocated, Is.LessThan(8000000), "100 metadata views of a 16 MB stash must not copy gigabytes of text");
    }
}
