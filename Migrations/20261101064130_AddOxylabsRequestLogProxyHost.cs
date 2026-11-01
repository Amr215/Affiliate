using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiliate.Migrations
{
    /// <inheritdoc />
    public partial class AddOxylabsRequestLogProxyHost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProxyHost",
                table: "OxylabsRequestLogs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OxylabsRequestLogs_ProxyHost",
                table: "OxylabsRequestLogs",
                column: "ProxyHost");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OxylabsRequestLogs_ProxyHost",
                table: "OxylabsRequestLogs");

            migrationBuilder.DropColumn(
                name: "ProxyHost",
                table: "OxylabsRequestLogs");
        }
    }
}
