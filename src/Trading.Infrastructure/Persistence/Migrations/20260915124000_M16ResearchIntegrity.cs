using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M16ResearchIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResearchRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    InstrumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Timeframe = table.Column<int>(type: "int", nullable: false),
                    FromUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    ToUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    DataSource = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DataVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CalendarId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DatasetSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ConfigurationSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ArtifactSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    SourceRevision = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ArtifactJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResearchRuns_Instruments_InstrumentId",
                        column: x => x.InstrumentId,
                        principalTable: "Instruments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchRuns_CreatedAtUtc",
                table: "ResearchRuns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchRuns_DatasetSha256_ConfigurationSha256_SourceRevision",
                table: "ResearchRuns",
                columns: new[] { "DatasetSha256", "ConfigurationSha256", "SourceRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResearchRuns_InstrumentId_Timeframe_FromUtc_ToUtc",
                table: "ResearchRuns",
                columns: new[] { "InstrumentId", "Timeframe", "FromUtc", "ToUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ResearchRuns");
        }
    }
}
