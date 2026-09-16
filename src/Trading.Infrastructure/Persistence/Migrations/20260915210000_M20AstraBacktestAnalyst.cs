using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Persistence.Migrations
{
    public partial class M20AstraBacktestAnalyst : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BacktestAnalyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResearchRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    Deployment = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ResponseModel = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProviderResponseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PromptSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ResearchArtifactSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    AnalysisSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    AnalysisJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacktestAnalyses", x => x.Id);
                    table.ForeignKey(name: "FK_BacktestAnalyses_ResearchRuns_ResearchRunId",
                        column: x => x.ResearchRunId, principalTable: "ResearchRuns", principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
            migrationBuilder.CreateIndex(name: "IX_BacktestAnalyses_CreatedAtUtc",
                table: "BacktestAnalyses", column: "CreatedAtUtc");
            migrationBuilder.CreateIndex(name: "IX_BacktestAnalyses_ResearchRunId_PromptSha256_Deployment",
                table: "BacktestAnalyses", columns: new[] { "ResearchRunId", "PromptSha256", "Deployment" }, unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropTable(name: "BacktestAnalyses");
    }
}
