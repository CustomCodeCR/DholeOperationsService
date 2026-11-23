using Npgsql;
using Microsoft.EntityFrameworkCore.Storage;

namespace Dhole.Operations.Persistence.Cases;

/// <summary>One atomic, PostgreSQL-owned global IDTRA counter for all aperture channels.</summary>
public static class OperationsIdtraAllocator
{
    public static async Task<long> NextAsync(OperationsDbContext db, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("IDTRA allocation requires an active PostgreSQL transaction.");
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO operations.operations_case_sequences (id, series_code, last_number, updated_at_utc)
            VALUES (@id, '00000', 1, now())
            ON CONFLICT (series_code)
            DO UPDATE SET last_number = operations_case_sequences.last_number + 1, updated_at_utc = now()
            RETURNING last_number;
            """, connection, (NpgsqlTransaction)db.Database.CurrentTransaction.GetDbTransaction());
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }
}
