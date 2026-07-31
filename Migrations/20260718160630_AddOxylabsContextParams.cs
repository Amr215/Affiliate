using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiliate.Migrations
{
    /// <inheritdoc />
    public partial class AddOxylabsContextParams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CategoryId",
                table: "ScraperSearches",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CheckEmptyGeo",
                table: "ScraperSearches",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "ScraperSearches",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ForceCookies",
                table: "ScraperSearches",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ForceHeaders",
                table: "ScraperSearches",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HcPolicy",
                table: "ScraperSearches",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxPrice",
                table: "ScraperSearches",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MerchantId",
                table: "ScraperSearches",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinPrice",
                table: "ScraperSearches",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Refinements",
                table: "ScraperSearches",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SafeSearch",
                table: "ScraperSearches",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "ScraperSearches",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CategoryId", "CheckEmptyGeo", "Currency", "ForceCookies", "ForceHeaders", "HcPolicy", "MaxPrice", "MerchantId", "MinPrice", "Refinements", "SafeSearch" },
                values: new object[] { null, null, null, false, false, true, null, null, null, null, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "ScraperSearches");

            migrationBuilder.DropColumn(
                name: "CheckEmptyGeo",
                table: "ScraperSearches");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "ScraperSearches");

            migrationBuilder.DropColumn(
                name: "ForceCookies",
                table: "ScraperSearches");

            migrationBuilder.DropColumn(
                name: "ForceHeaders",
                table: "ScraperSearches");

            migrationBuilder.DropColumn(
                name: "HcPolicy",
                table: "ScraperSearches");

            migrationBuilder.DropColumn(
                name: "MaxPrice",
                table: "ScraperSearches");

            migrationBuilder.DropColumn(
                name: "MerchantId",
                table: "ScraperSearches");

            migrationBuilder.DropColumn(
                name: "MinPrice",
                table: "ScraperSearches");

            migrationBuilder.DropColumn(
                name: "Refinements",
                table: "ScraperSearches");

            migrationBuilder.DropColumn(
                name: "SafeSearch",
                table: "ScraperSearches");
        }
    }
}
