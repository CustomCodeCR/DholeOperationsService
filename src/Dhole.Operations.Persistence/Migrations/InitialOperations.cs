using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611120001_InitialOperations")]
public sealed class InitialOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "operations_outbox",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EventName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                PayloadJson = table.Column<string>(type: "text", nullable: false),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RetryCount = table.Column<int>(type: "integer", nullable: false),
                LastError = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_operations_outbox", x => x.Id));
        migrationBuilder.CreateIndex(
            name: "IX_operations_outbox_PublishedAtUtc_OccurredAtUtc",
            table: "operations_outbox",
            columns: new[] { "PublishedAtUtc", "OccurredAtUtc" });

        migrationBuilder.CreateTable(
            name: "operations_inbox",
            columns: table => new
            {
                ConsumerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                EventId = table.Column<Guid>(type: "uuid", nullable: false),
                EventName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                PayloadJson = table.Column<string>(type: "text", nullable: false),
                ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_operations_inbox", x => new { x.ConsumerName, x.EventId }));
        migrationBuilder.CreateIndex(
            name: "IX_operations_inbox_ProcessedAtUtc",
            table: "operations_inbox",
            column: "ProcessedAtUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("operations_inbox");
        migrationBuilder.DropTable("operations_outbox");
    }
}
