using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiliate.Migrations
{
    /// <inheritdoc />
    public partial class AddProductScraperSearchId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ScraperSearchId",
                table: "Products",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_ScraperSearchId",
                table: "Products",
                column: "ScraperSearchId");

            migrationBuilder.AddForeignKey(
                name: "FK_Products_ScraperSearches_ScraperSearchId",
                table: "Products",
                column: "ScraperSearchId",
                principalTable: "ScraperSearches",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Products_ScraperSearches_ScraperSearchId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_ScraperSearchId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ScraperSearchId",
                table: "Products");
        }
    }
}
