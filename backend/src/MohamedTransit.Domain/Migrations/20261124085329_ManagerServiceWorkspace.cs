using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MohamedTransit.Domain.Migrations
{
    /// <inheritdoc />
    public partial class ManagerServiceWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DocumentTypeLabel",
                table: "StageDocuments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CarrierOrVessel",
                table: "Shipments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "CompletionPercentage",
                table: "Shipments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ConsignmentDetails",
                table: "Shipments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ETA",
                table: "Shipments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogisticsRouteSummary",
                table: "Shipments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ManagerRiskChannel",
                table: "Shipments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReferenceNumber",
                table: "Shipments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ServiceCode",
                table: "Shipments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "SlaDeadline",
                table: "Shipments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Shipments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WorkflowStatus",
                table: "Shipments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CompletedByName",
                table: "ServiceStageExecution",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CompletedByUserId",
                table: "ServiceStageExecution",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskChannel",
                table: "ServiceStageExecution",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StageNote",
                table: "ServiceStageExecution",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StageNumber",
                table: "ServiceStageExecution",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SummaryNote",
                table: "ServiceStageExecution",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ServiceActivityLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServiceId = table.Column<long>(type: "INTEGER", nullable: false),
                    StageId = table.Column<long>(type: "INTEGER", nullable: true),
                    ActionTitle = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    ActorName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RecordStatus = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceActivityLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StageDiscussions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServiceId = table.Column<long>(type: "INTEGER", nullable: false),
                    StageId = table.Column<long>(type: "INTEGER", nullable: false),
                    AuthorUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    AuthorName = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorRole = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecordStatus = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageDiscussions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StageRequiredDocuments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServiceStageExecutionId = table.Column<long>(type: "INTEGER", nullable: false),
                    DocumentType = table.Column<string>(type: "TEXT", nullable: false),
                    CreateAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RecordStatus = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageRequiredDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StageRequiredDocuments_ServiceStageExecution_ServiceStageExecutionId",
                        column: x => x.ServiceStageExecutionId,
                        principalTable: "ServiceStageExecution",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StageRequiredDocuments_ServiceStageExecutionId",
                table: "StageRequiredDocuments",
                column: "ServiceStageExecutionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServiceActivityLogs");

            migrationBuilder.DropTable(
                name: "StageDiscussions");

            migrationBuilder.DropTable(
                name: "StageRequiredDocuments");

            migrationBuilder.DropColumn(
                name: "DocumentTypeLabel",
                table: "StageDocuments");

            migrationBuilder.DropColumn(
                name: "CarrierOrVessel",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "CompletionPercentage",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "ConsignmentDetails",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "ETA",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "LogisticsRouteSummary",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "ManagerRiskChannel",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "ReferenceNumber",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "ServiceCode",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "SlaDeadline",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "WorkflowStatus",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "CompletedByName",
                table: "ServiceStageExecution");

            migrationBuilder.DropColumn(
                name: "CompletedByUserId",
                table: "ServiceStageExecution");

            migrationBuilder.DropColumn(
                name: "RiskChannel",
                table: "ServiceStageExecution");

            migrationBuilder.DropColumn(
                name: "StageNote",
                table: "ServiceStageExecution");

            migrationBuilder.DropColumn(
                name: "StageNumber",
                table: "ServiceStageExecution");

            migrationBuilder.DropColumn(
                name: "SummaryNote",
                table: "ServiceStageExecution");
        }
    }
}
