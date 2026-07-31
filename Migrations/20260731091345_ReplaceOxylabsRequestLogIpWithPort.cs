using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiliate.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceOxylabsRequestLogIpWithPort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OxylabsRequestLogs_Ip",
                table: "OxylabsRequestLogs");

            migrationBuilder.DropColumn(
                name: "Ip",
                table: "OxylabsRequestLogs");

            migrationBuilder.AddColumn<int>(
                name: "Port",
                table: "OxylabsRequestLogs",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OxylabsRequestLogs_Port",
                table: "OxylabsRequestLogs",
                column: "Port");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OxylabsRequestLogs_Port",
                table: "OxylabsRequestLogs");

            migrationBuilder.DropColumn(
                name: "Port",
                table: "OxylabsRequestLogs");

            migrationBuilder.AddColumn<string>(
                name: "Ip",
                table: "OxylabsRequestLogs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OxylabsRequestLogs_Ip",
                table: "OxylabsRequestLogs",
                column: "Ip");
        }
    }
}
