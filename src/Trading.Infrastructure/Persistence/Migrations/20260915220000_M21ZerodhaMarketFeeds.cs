using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Persistence.Migrations
{
    public partial class M21ZerodhaMarketFeeds : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketFeedCaptures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    QuoteMode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TickCount = table.Column<int>(type: "int", nullable: false),
                    FirstReceivedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    LastReceivedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    ArtifactSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ArtifactJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketFeedCaptures", x => x.Id);
                    table.CheckConstraint("CK_MarketFeedCaptures_TickCount", "[TickCount] > 0");
                });
            migrationBuilder.CreateIndex(name: "IX_MarketFeedCaptures_CreatedAtUtc",
                table: "MarketFeedCaptures", column: "CreatedAtUtc");
            migrationBuilder.CreateIndex(name: "IX_MarketFeedCaptures_Source_CreatedAtUtc",
                table: "MarketFeedCaptures", columns: new[] { "Source", "CreatedAtUtc" });
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropTable(name: "MarketFeedCaptures");
    }
}
