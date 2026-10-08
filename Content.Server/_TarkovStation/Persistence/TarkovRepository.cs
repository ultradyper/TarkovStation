// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;

namespace Content.Server._TarkovStation.Persistence;

/// <summary>
/// A dedicated SQLite state document and operation journal. Mutations operate on a copy and commit
/// together with their idempotency key. Failed validations cannot leave a partially modified balance.
/// World entities are projections of this state; persisted item IDs identify pending deliveries.
/// </summary>
public sealed class TarkovRepository : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private TarkovData _data;
    private static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(TarkovStoredItem)) return;
            var snapshot = info.Properties.First(p => p.Name == nameof(TarkovStoredItem.Snapshot));
            // Keep reading embedded legacy snapshots, but write blobs into their dedicated SQLite table.
            snapshot.ShouldSerialize = (_, _) => false;
        });
        return new JsonSerializerOptions { IncludeFields = true, TypeInfoResolver = resolver };
    }

    public TarkovRepository(string path, long now, int duration)
    {
        if (path != ":memory:")
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _connection.Open();
        using var setup = _connection.CreateCommand();
        setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; "
            + "CREATE TABLE IF NOT EXISTS tarkov_state (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL); "
            + "CREATE TABLE IF NOT EXISTS tarkov_operations (cycle TEXT NOT NULL, actor TEXT NOT NULL, operation TEXT NOT NULL, "
            + "kind TEXT NOT NULL, utc INTEGER NOT NULL, PRIMARY KEY(cycle,actor,operation)); "
            + "CREATE TABLE IF NOT EXISTS tarkov_item_snapshots (id TEXT PRIMARY KEY, snapshot TEXT NOT NULL);";
        setup.ExecuteNonQuery();
        using var read = _connection.CreateCommand();
        read.CommandText = "SELECT payload FROM tarkov_state WHERE id=1";
        var stored = read.ExecuteScalar() as string;
        _data = stored == null ? new TarkovData { EndsUtc = now + duration } : Decode(stored);
        var embedded = _data.Items.Values.Any(i => i.Snapshot != "");
        using (var snapshots = _connection.CreateCommand())
        {
            snapshots.CommandText = "SELECT id,snapshot FROM tarkov_item_snapshots";
            using var rows = snapshots.ExecuteReader();
            while (rows.Read())
                if (_data.Items.TryGetValue(rows.GetString(0), out var item)) item.Snapshot = rows.GetString(1);
        }
        // Legacy embedded blobs and their metadata move atomically. Reopening does not reset any progress.
        if (stored == null || embedded)
        {
            using var migration = _connection.BeginTransaction();
            Write(_data, null, migration);
            migration.Commit();
        }
    }

    public TarkovData Read()
    {
        lock (_gate)
            return _data.Copy();
    }

    /// <returns>Null on a committed success, a localization key on rejection/replay.</returns>
    public string? Execute(string actor, string operation, string kind, Func<TarkovData, string?> mutation, long now)
    {
        if (string.IsNullOrWhiteSpace(operation) || operation.Length > 96 || actor.Length > 64)
            return "tarkov-error-request";
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var prior = _connection.CreateCommand();
            prior.Transaction = transaction;
            prior.CommandText = "SELECT 1 FROM tarkov_operations WHERE cycle=$cycle AND actor=$actor AND operation=$op";
            prior.Parameters.AddWithValue("$cycle", _data.Cycle);
            prior.Parameters.AddWithValue("$actor", actor);
            prior.Parameters.AddWithValue("$op", operation);
            if (prior.ExecuteScalar() != null)
                return "tarkov-error-replay";
            var candidate = _data.Copy();
            var error = mutation(candidate);
            if (error != null)
                return error;
            Validate(candidate);
            Write(candidate, _data, transaction);
            using var journal = _connection.CreateCommand();
            journal.Transaction = transaction;
            journal.CommandText = "INSERT INTO tarkov_operations(cycle,actor,operation,kind,utc) VALUES($cycle,$actor,$op,$kind,$utc)";
            journal.Parameters.AddWithValue("$cycle", candidate.Cycle);
            journal.Parameters.AddWithValue("$actor", actor);
            journal.Parameters.AddWithValue("$op", operation);
            journal.Parameters.AddWithValue("$kind", kind);
            journal.Parameters.AddWithValue("$utc", now);
            journal.ExecuteNonQuery();
            transaction.Commit();
            _data = candidate;
            return null;
        }
    }

    private void Write(TarkovData data, TarkovData? previous, SqliteTransaction transaction)
    {
        if (previous != null && previous.Cycle != data.Cycle)
        {
            using var wipe = _connection.CreateCommand();
            wipe.Transaction = transaction;
            wipe.CommandText = "DELETE FROM tarkov_item_snapshots";
            wipe.ExecuteNonQuery();
        }
        foreach (var item in data.Items.Values)
        {
            if (item.Snapshot == "" || (previous != null && previous.Items.TryGetValue(item.Id, out var old) && old.Snapshot == item.Snapshot)) continue;
            using var blob = _connection.CreateCommand();
            blob.Transaction = transaction;
            blob.CommandText = "INSERT INTO tarkov_item_snapshots(id,snapshot) VALUES($id,$snapshot) "
                + "ON CONFLICT(id) DO UPDATE SET snapshot=excluded.snapshot";
            blob.Parameters.AddWithValue("$id", item.Id);
            blob.Parameters.AddWithValue("$snapshot", item.Snapshot);
            blob.ExecuteNonQuery();
        }
        using var write = _connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "INSERT INTO tarkov_state(id,payload) VALUES(1,$payload) "
            + "ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
        write.Parameters.AddWithValue("$payload", Encode(data));
        write.ExecuteNonQuery();
    }

    private static void Validate(TarkovData data)
    {
        foreach (var account in data.Accounts.Values)
        {
            if (account.Balance < 0 || account.Reserved < 0 || account.Balance > 1_000_000_000 || account.Reserved > 1_000_000_000)
                throw new InvalidOperationException("Tarkov wallet invariant violated");
        }
        foreach (var item in data.Items.Values)
        {
            if (item.Owner != "" && !data.Accounts.ContainsKey(item.Owner))
                throw new InvalidOperationException("Tarkov item owner missing");
        }
    }

    private static string Encode(TarkovData data) => JsonSerializer.Serialize(data, Json);
    private static TarkovData Decode(string text) => JsonSerializer.Deserialize<TarkovData>(text, Json)
        ?? throw new InvalidDataException("Empty Tarkov state document");
    public void Dispose() => _connection.Dispose();
}
