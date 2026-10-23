using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiliate.Migrations
{
    /// <inheritdoc />
    public partial class AddScraperUrlLongUnavailableRecheckIntervalMinutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LongUnavailableRecheckIntervalMinutes",
                table: "ScraperUrls",
                type: "int",
                nullable: false,
                defaultValue: 180);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LongUnavailableRecheckIntervalMinutes",
                table: "ScraperUrls");
        }
    }
}
