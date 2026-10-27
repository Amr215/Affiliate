using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiliate.Migrations
{
    /// <inheritdoc />
    public partial class AddPublishAuditLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublishAuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Asin = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    EventType = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Route = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ProxyHost = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ProxyPort = table.Column<int>(type: "int", nullable: true),
                    PageKind = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    Success = table.Column<bool>(type: "bit", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ChatId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DraftToken = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublishAuditLogs_Asin_OccurredAt",
                table: "PublishAuditLogs",
                columns: new[] { "Asin", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PublishAuditLogs_CorrelationId",
                table: "PublishAuditLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_PublishAuditLogs_OccurredAt",
                table: "PublishAuditLogs",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublishAuditLogs");
        }
    }
}
