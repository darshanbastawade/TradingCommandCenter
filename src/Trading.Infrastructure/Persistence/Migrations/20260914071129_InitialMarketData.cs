using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMarketData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Instruments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Exchange = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    LotSize = table.Column<int>(type: "int", nullable: false),
                    TickSize = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instruments", x => x.Id);
                    table.CheckConstraint("CK_Instruments_LotSize", "[LotSize] > 0");
                    table.CheckConstraint("CK_Instruments_TickSize", "[TickSize] > 0");
                });

            migrationBuilder.CreateTable(
                name: "Candles",
                columns: table => new
                {
                    InstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Timeframe = table.Column<int>(type: "int", nullable: false),
                    OpenTimeUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    Open = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    High = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Low = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Close = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    OpenInterest = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Candles", x => new { x.InstrumentId, x.Timeframe, x.OpenTimeUtc });
                    table.CheckConstraint("CK_Candles_OpenInterest", "[OpenInterest] IS NULL OR [OpenInterest] >= 0");
                    table.CheckConstraint("CK_Candles_Prices", "[Low] > 0 AND [High] >= [Low] AND [Open] >= [Low] AND [Open] <= [High] AND [Close] >= [Low] AND [Close] <= [High]");
                    table.CheckConstraint("CK_Candles_Timeframe", "[Timeframe] IN (1, 3, 5, 15, 30, 60, 1440)");
                    table.CheckConstraint("CK_Candles_Volume", "[Volume] >= 0");
                    table.ForeignKey(
                        name: "FK_Candles_Instruments_InstrumentId",
                        column: x => x.InstrumentId,
                        principalTable: "Instruments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_Exchange_Symbol",
                table: "Instruments",
                columns: new[] { "Exchange", "Symbol" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Candles");

            migrationBuilder.DropTable(
                name: "Instruments");
        }
    }
}
