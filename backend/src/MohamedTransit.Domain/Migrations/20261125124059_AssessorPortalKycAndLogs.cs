using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MohamedTransit.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AssessorPortalKycAndLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviousReviewNote",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationStatus",
                table: "Customers",
                type: "TEXT",
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<string>(
                name: "VerificationStatus",
                table: "CustomerDocuments",
                type: "TEXT",
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAt",
                table: "CustomerDocuments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE Customers
                SET VerificationStatus = CASE
                    WHEN IsVerified = TRUE THEN 'Verified'
                    WHEN IsReturnedForRevision = TRUE THEN 'RequestChanges'
                    WHEN RecordStatus = 1 THEN 'Rejected'
                    ELSE 'Pending'
                END
                """);
            migrationBuilder.Sql("""
                UPDATE CustomerDocuments
                SET VerificationStatus = CASE
                    WHEN IsVerified = TRUE THEN 'Verified'
                    ELSE 'Pending'
                END
                """);

            migrationBuilder.CreateTable(
                name: "AssessmentLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ActorUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorName = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<long>(type: "INTEGER", nullable: true),
                    DocumentId = table.Column<long>(type: "INTEGER", nullable: true),
                    ServiceId = table.Column<long>(type: "INTEGER", nullable: true),
                    StageId = table.Column<long>(type: "INTEGER", nullable: true),
                    Reference = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RecordStatus = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssessmentLogs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssessmentLogs");

            migrationBuilder.DropColumn(
                name: "PreviousReviewNote",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "VerificationStatus",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "VerificationStatus",
                table: "CustomerDocuments");

            migrationBuilder.DropColumn(
                name: "VerifiedAt",
                table: "CustomerDocuments");
        }
    }
}
