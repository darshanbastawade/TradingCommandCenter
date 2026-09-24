using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M38AuthoritativeReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InternalTradingLedgerSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AsOfUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    StrategyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Revision = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ArtifactSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ArtifactJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalTradingLedgerSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InternalTradingLedgerSnapshots_ArtifactSha256",
                table: "InternalTradingLedgerSnapshots",
                column: "ArtifactSha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InternalTradingLedgerSnapshots_StrategyId_AsOfUtc",
                table: "InternalTradingLedgerSnapshots",
                columns: new[] { "StrategyId", "AsOfUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InternalTradingLedgerSnapshots");
        }
    }
}
