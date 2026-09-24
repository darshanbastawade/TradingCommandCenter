using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Persistence.Migrations;

public partial class M38DurableExecutionState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ReconciledExecutionStates",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AsOfUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                ExchangeTradingDate = table.Column<DateOnly>(type: "date", nullable: false),
                RealizedPnlToday = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                UnresolvedBrokerSubmissions = table.Column<int>(type: "int", nullable: false),
                ActiveOpenBrokerPositions = table.Column<int>(type: "int", nullable: false),
                ReconciliationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ReconciliationSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                SourceRevision = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Authoritative = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReconciledExecutionStates", x => x.Id);
                table.CheckConstraint("CK_ReconciledExecutionStates_Counts",
                    "[UnresolvedBrokerSubmissions] >= 0 AND [ActiveOpenBrokerPositions] >= 0");
            });
        migrationBuilder.CreateIndex(
            name: "IX_ReconciledExecutionStates_ExchangeTradingDate_AsOfUtc",
            table: "ReconciledExecutionStates", columns: new[] { "ExchangeTradingDate", "AsOfUtc" }, unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_ReconciledExecutionStates_ReconciliationId",
            table: "ReconciledExecutionStates", column: "ReconciliationId", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ReconciledExecutionStates");
}
