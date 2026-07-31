using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiliate.Migrations
{
    /// <inheritdoc />
    public partial class RemoveBooksEgyptSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "ScraperSearches",
                keyColumn: "Id",
                keyValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "ScraperSearches",
                columns: new[] { "Id", "CategoryId", "CheckEmptyGeo", "CreatedAt", "Currency", "Domain", "ForceCookies", "ForceHeaders", "GeoLocation", "HcPolicy", "IntervalSeconds", "IsEnabled", "LastRunAt", "LastRunError", "Locale", "MaxPrice", "MerchantId", "MinPrice", "Name", "Parse", "Query", "Refinements", "SafeSearch", "SortBy", "Source", "StartPage" },
                values: new object[] { 1, null, null, new DateTime(2026, 7, 17, 0, 0, 0, 0, DateTimeKind.Utc), null, "eg", false, false, "Cairo", true, 600, true, null, null, "ar-EG", null, null, null, "Books Egypt (price low to high)", true, "book", null, true, "price_low_to_high", "amazon_search", 1 });
        }
    }
}
