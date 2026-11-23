using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Dhole.Operations.Persistence.Cases;

/// <summary>Single atomic IDTRA allocation for manual and accepted-QUO openings.</summary>
public static class OperationsIdtraSequence
{
    public static async Task<long> NextAsync(OperationsDbContext db, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var command = new NpgsqlCommand("""
            INSERT INTO operations.operations_case_sequences (id, series_code, last_number, updated_at_utc)
            VALUES (@id, '00000', 1, now())
            ON CONFLICT (series_code)
            DO UPDATE SET last_number = operations_case_sequences.last_number + 1, updated_at_utc = now()
            RETURNING last_number;
            """, connection, (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
}
