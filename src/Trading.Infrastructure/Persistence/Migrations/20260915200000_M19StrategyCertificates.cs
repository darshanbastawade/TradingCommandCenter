using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Persistence.Migrations
{
    public partial class M19StrategyCertificates : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StrategyCertificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResearchRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StrategyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ResearchArtifactSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CertificateSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CertificateJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyCertificates", x => x.Id);
                    table.ForeignKey(name: "FK_StrategyCertificates_ResearchRuns_ResearchRunId",
                        column: x => x.ResearchRunId, principalTable: "ResearchRuns", principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(name: "IX_StrategyCertificates_ExpiresAtUtc",
                table: "StrategyCertificates", column: "ExpiresAtUtc");
            migrationBuilder.CreateIndex(name: "IX_StrategyCertificates_ResearchRunId_StrategyId",
                table: "StrategyCertificates", columns: new[] { "ResearchRunId", "StrategyId" }, unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropTable(name: "StrategyCertificates");
    }
}
