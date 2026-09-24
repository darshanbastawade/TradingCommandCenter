using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M38PaperQualificationLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "QualificationCertificateId",
                table: "PaperTradingSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualificationCertificateSha256",
                table: "PaperTradingSessions",
                type: "nchar(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QualificationStartedAtUtc",
                table: "PaperTradingSessions",
                type: "datetime2(7)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StrategyQualificationId",
                table: "PaperTradingSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StrategyQualificationSha256",
                table: "PaperTradingSessions",
                type: "nchar(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaperTradingSessions_StrategyQualificationId_CreatedAtUtc",
                table: "PaperTradingSessions",
                columns: new[] { "StrategyQualificationId", "CreatedAtUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaperTradingSessions_QualificationLineage",
                table: "PaperTradingSessions",
                sql: "([StrategyQualificationId] IS NULL AND [StrategyQualificationSha256] IS NULL AND [QualificationCertificateId] IS NULL AND [QualificationCertificateSha256] IS NULL AND [QualificationStartedAtUtc] IS NULL) OR ([StrategyQualificationId] IS NOT NULL AND [StrategyQualificationSha256] IS NOT NULL AND [QualificationCertificateId] IS NOT NULL AND [QualificationCertificateSha256] IS NOT NULL AND [QualificationStartedAtUtc] IS NOT NULL AND [CreatedAtUtc] >= [QualificationStartedAtUtc])");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaperTradingSessions_StrategyQualificationId_CreatedAtUtc",
                table: "PaperTradingSessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaperTradingSessions_QualificationLineage",
                table: "PaperTradingSessions");

            migrationBuilder.DropColumn(
                name: "QualificationCertificateId",
                table: "PaperTradingSessions");

            migrationBuilder.DropColumn(
                name: "QualificationCertificateSha256",
                table: "PaperTradingSessions");

            migrationBuilder.DropColumn(
                name: "QualificationStartedAtUtc",
                table: "PaperTradingSessions");

            migrationBuilder.DropColumn(
                name: "StrategyQualificationId",
                table: "PaperTradingSessions");

            migrationBuilder.DropColumn(
                name: "StrategyQualificationSha256",
                table: "PaperTradingSessions");
        }
    }
}
