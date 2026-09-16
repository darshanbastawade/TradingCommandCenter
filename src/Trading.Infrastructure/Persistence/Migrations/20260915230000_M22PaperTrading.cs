using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Persistence.Migrations
{
    public partial class M22PaperTrading : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaperTradingSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StrategyCertificateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MarketFeedCaptureId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    StrategyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    InitialCash = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    EndingCash = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    RealizedNetPnl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    SubmittedOrders = table.Column<int>(type: "int", nullable: false),
                    FilledTrades = table.Column<int>(type: "int", nullable: false),
                    RejectedOrders = table.Column<int>(type: "int", nullable: false),
                    ConfigurationSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ArtifactSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ArtifactJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperTradingSessions", x => x.Id);
                    table.CheckConstraint("CK_PaperTradingSessions_Cash", "[InitialCash] > 0 AND [EndingCash] >= 0");
                    table.CheckConstraint("CK_PaperTradingSessions_Counts", "[SubmittedOrders] > 0 AND [FilledTrades] >= 0 AND [RejectedOrders] >= 0 AND [FilledTrades] + [RejectedOrders] = [SubmittedOrders]");
                    table.ForeignKey(name: "FK_PaperTradingSessions_MarketFeedCaptures_MarketFeedCaptureId",
                        column: x => x.MarketFeedCaptureId, principalTable: "MarketFeedCaptures", principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(name: "FK_PaperTradingSessions_StrategyCertificates_StrategyCertificateId",
                        column: x => x.StrategyCertificateId, principalTable: "StrategyCertificates", principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
            migrationBuilder.CreateIndex(name: "IX_PaperTradingSessions_CreatedAtUtc",
                table: "PaperTradingSessions", column: "CreatedAtUtc");
            migrationBuilder.CreateIndex(name: "IX_PaperTradingSessions_MarketFeedCaptureId",
                table: "PaperTradingSessions", column: "MarketFeedCaptureId");
            migrationBuilder.CreateIndex(name: "IX_PaperTradingSessions_StrategyCertificateId_MarketFeedCaptureId_ConfigurationSha256",
                table: "PaperTradingSessions", columns: new[] { "StrategyCertificateId", "MarketFeedCaptureId", "ConfigurationSha256" }, unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropTable(name: "PaperTradingSessions");
    }
}
