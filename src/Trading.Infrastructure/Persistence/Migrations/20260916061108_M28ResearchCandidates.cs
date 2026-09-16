using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M28ResearchCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ParameterSweeps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    StrategyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    WorkerId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WorkerVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BaseSpecificationSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    DatasetSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    GridSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    EvaluatedCandidates = table.Column<int>(type: "int", nullable: false),
                    StoredCandidates = table.Column<int>(type: "int", nullable: false),
                    ArtifactSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ArtifactJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NativeVerificationCompleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParameterSweeps", x => x.Id);
                    table.CheckConstraint("CK_ParameterSweeps_Counts", "[EvaluatedCandidates] > 0 AND [StoredCandidates] > 0 AND [StoredCandidates] <= [EvaluatedCandidates]");
                });

            migrationBuilder.CreateTable(
                name: "BacktestCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParameterSweepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    StrategyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SpecificationSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    DatasetSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CandidateSpecificationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResearchScore = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    ResearchMetricsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResearchEvidenceSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    VerifiedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: true),
                    NativeEngineId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NativeEngineVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NativeResultSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NativeNetPnl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    NativeTradeCount = table.Column<int>(type: "int", nullable: true),
                    NativeRunJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VerificationFailure = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacktestCandidates", x => x.Id);
                    table.CheckConstraint("CK_BacktestCandidates_Rank", "[Rank] > 0");
                    table.CheckConstraint("CK_BacktestCandidates_Status", "[Status] IN ('ResearchProposed', 'NativeVerified', 'NativeFailed')");
                    table.CheckConstraint("CK_BacktestCandidates_TradeCount", "[NativeTradeCount] IS NULL OR [NativeTradeCount] >= 0");
                    table.ForeignKey(
                        name: "FK_BacktestCandidates_ParameterSweeps_ParameterSweepId",
                        column: x => x.ParameterSweepId,
                        principalTable: "ParameterSweeps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NativeCandidateVerificationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParameterSweepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    EngineId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EngineVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequestedCandidates = table.Column<int>(type: "int", nullable: false),
                    VerifiedCandidates = table.Column<int>(type: "int", nullable: false),
                    FailedCandidates = table.Column<int>(type: "int", nullable: false),
                    ArtifactSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ArtifactJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NativeCandidateVerificationRuns", x => x.Id);
                    table.CheckConstraint("CK_NativeCandidateVerificationRuns_Counts", "[RequestedCandidates] > 0 AND [VerifiedCandidates] >= 0 AND [FailedCandidates] >= 0 AND [VerifiedCandidates] + [FailedCandidates] = [RequestedCandidates]");
                    table.ForeignKey(
                        name: "FK_NativeCandidateVerificationRuns_ParameterSweeps_ParameterSweepId",
                        column: x => x.ParameterSweepId,
                        principalTable: "ParameterSweeps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestCandidates_ParameterSweepId_Rank",
                table: "BacktestCandidates",
                columns: new[] { "ParameterSweepId", "Rank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BacktestCandidates_ParameterSweepId_SpecificationSha256",
                table: "BacktestCandidates",
                columns: new[] { "ParameterSweepId", "SpecificationSha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NativeCandidateVerificationRuns_ParameterSweepId",
                table: "NativeCandidateVerificationRuns",
                column: "ParameterSweepId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParameterSweeps_BaseSpecificationSha256_GridSha256_WorkerId_WorkerVersion",
                table: "ParameterSweeps",
                columns: new[] { "BaseSpecificationSha256", "GridSha256", "WorkerId", "WorkerVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParameterSweeps_CreatedAtUtc",
                table: "ParameterSweeps",
                column: "CreatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BacktestCandidates");

            migrationBuilder.DropTable(
                name: "NativeCandidateVerificationRuns");

            migrationBuilder.DropTable(
                name: "ParameterSweeps");
        }
    }
}
