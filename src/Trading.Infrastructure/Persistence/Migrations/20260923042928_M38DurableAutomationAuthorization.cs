using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M38DurableAutomationAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ControlledAutomationAuthorizations",
                columns: table => new
                {
                    AutomationDecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AutomationSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ActionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActionReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    StrategyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EvaluatedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MaximumAuthorizedActions = table.Column<int>(type: "int", nullable: false),
                    ConsumedActions = table.Column<int>(type: "int", nullable: false),
                    FirstConsumedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    LastConsumedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    RelatedLiveOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlledAutomationAuthorizations", x => x.AutomationDecisionId);
                    table.CheckConstraint("CK_ControlledAutomationAuthorizations_Actions", "[MaximumAuthorizedActions] = 1 AND [ConsumedActions] = 1");
                    table.CheckConstraint("CK_ControlledAutomationAuthorizations_Decision", "[Decision] = 'DirectSubmissionEligible'");
                    table.CheckConstraint("CK_ControlledAutomationAuthorizations_Validity", "[EvaluatedAtUtc] <= [FirstConsumedAtUtc] AND [FirstConsumedAtUtc] < [ExpiresAtUtc]");
                    table.ForeignKey(
                        name: "FK_ControlledAutomationAuthorizations_LiveOrders_RelatedLiveOrderId",
                        column: x => x.RelatedLiveOrderId,
                        principalTable: "LiveOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ControlledAutomationAuthorizations_ActionId",
                table: "ControlledAutomationAuthorizations",
                column: "ActionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ControlledAutomationAuthorizations_AutomationSha256",
                table: "ControlledAutomationAuthorizations",
                column: "AutomationSha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ControlledAutomationAuthorizations_RelatedLiveOrderId",
                table: "ControlledAutomationAuthorizations",
                column: "RelatedLiveOrderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ControlledAutomationAuthorizations");
        }
    }
}
