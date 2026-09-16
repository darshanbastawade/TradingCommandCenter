using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Persistence.Migrations
{
    public partial class M17RealOptionsBacktesting : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OptionContracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnderlyingInstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Exchange = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Strike = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Right = table.Column<int>(type: "int", nullable: false),
                    LotSize = table.Column<int>(type: "int", nullable: false),
                    TickSize = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionContracts", x => x.Id);
                    table.CheckConstraint("CK_OptionContracts_LotSize", "[LotSize] > 0");
                    table.CheckConstraint("CK_OptionContracts_Right", "[Right] IN (1, 2)");
                    table.CheckConstraint("CK_OptionContracts_Strike", "[Strike] > 0");
                    table.CheckConstraint("CK_OptionContracts_TickSize", "[TickSize] > 0");
                    table.ForeignKey(name: "FK_OptionContracts_Instruments_UnderlyingInstrumentId",
                        column: x => x.UnderlyingInstrumentId, principalTable: "Instruments",
                        principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OptionQuotes",
                columns: table => new
                {
                    OptionContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    Bid = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Ask = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Last = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    OpenInterest = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionQuotes", x => new { x.OptionContractId, x.TimestampUtc });
                    table.CheckConstraint("CK_OptionQuotes_OpenInterest", "[OpenInterest] >= 0");
                    table.CheckConstraint("CK_OptionQuotes_Prices", "[Bid] > 0 AND [Ask] >= [Bid] AND [Last] > 0");
                    table.CheckConstraint("CK_OptionQuotes_Volume", "[Volume] >= 0");
                    table.ForeignKey(name: "FK_OptionQuotes_OptionContracts_OptionContractId",
                        column: x => x.OptionContractId, principalTable: "OptionContracts",
                        principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(name: "IX_OptionContracts_Symbol", table: "OptionContracts",
                column: "Symbol", unique: true);
            migrationBuilder.CreateIndex(name: "IX_OptionContracts_UnderlyingInstrumentId_ExpiryDate_Right_Strike",
                table: "OptionContracts", columns: new[] { "UnderlyingInstrumentId", "ExpiryDate", "Right", "Strike" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_OptionQuotes_TimestampUtc", table: "OptionQuotes", column: "TimestampUtc");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "OptionQuotes");
            migrationBuilder.DropTable(name: "OptionContracts");
        }
    }
}
