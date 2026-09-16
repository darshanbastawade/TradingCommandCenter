using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Persistence.Migrations
{
    public partial class M23RiskGatedLiveTrading : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LiveOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StrategyCertificateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StrategyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Exchange = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TradingSymbol = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    LimitPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    StopPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    TargetPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    PaperEvidenceSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    RiskDecisionSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    BrokerOrderId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ArtifactSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ArtifactJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveOrders", x => x.Id);
                    table.CheckConstraint("CK_LiveOrders_Mode", "[Mode] IN ('SemiLive', 'DirectLive')");
                    table.CheckConstraint("CK_LiveOrders_Status", "[Status] IN ('Proposed', 'Prepared', 'Submitted')");
                    table.CheckConstraint("CK_LiveOrders_Prices", "[Quantity] > 0 AND [StopPrice] > 0 AND [StopPrice] < [LimitPrice] AND [TargetPrice] > [LimitPrice]");
                    table.ForeignKey(name: "FK_LiveOrders_StrategyCertificates_StrategyCertificateId",
                        column: x => x.StrategyCertificateId, principalTable: "StrategyCertificates", principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
            migrationBuilder.CreateIndex(name: "IX_LiveOrders_CreatedAtUtc", table: "LiveOrders", column: "CreatedAtUtc");
            migrationBuilder.CreateIndex(name: "IX_LiveOrders_RequestId", table: "LiveOrders", column: "RequestId", unique: true);
            migrationBuilder.CreateIndex(name: "IX_LiveOrders_StrategyCertificateId", table: "LiveOrders", column: "StrategyCertificateId");
        }

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "LiveOrders");
    }
}
