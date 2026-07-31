using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiliate.Migrations
{
    /// <inheritdoc />
    public partial class AddOxylabsRequestLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OxylabsRequestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScraperSearchId = table.Column<int>(type: "int", nullable: false),
                    Page = table.Column<int>(type: "int", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: false),
                    RequestBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StatusPhrase = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OxylabsRequestLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OxylabsRequestLogs_ScraperSearches_ScraperSearchId",
                        column: x => x.ScraperSearchId,
                        principalTable: "ScraperSearches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OxylabsRequestLogs_RequestedAt",
                table: "OxylabsRequestLogs",
                column: "RequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OxylabsRequestLogs_ScraperSearchId",
                table: "OxylabsRequestLogs",
                column: "ScraperSearchId");

            migrationBuilder.CreateIndex(
                name: "IX_OxylabsRequestLogs_ScraperSearchId_RequestedAt",
                table: "OxylabsRequestLogs",
                columns: new[] { "ScraperSearchId", "RequestedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OxylabsRequestLogs");
        }
    }
}
