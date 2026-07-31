using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiliate.Migrations
{
    /// <inheritdoc />
    public partial class AddScraperSearches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScraperSearches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Domain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Query = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Locale = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StartPage = table.Column<int>(type: "int", nullable: false),
                    Pages = table.Column<int>(type: "int", nullable: false),
                    Parse = table.Column<bool>(type: "bit", nullable: false),
                    SortBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    GeoLocation = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRunError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Interval = table.Column<TimeSpan>(type: "time", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScraperSearches", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "ScraperSearches",
                columns: new[] { "Id", "CreatedAt", "Domain", "GeoLocation", "Interval", "IntervalSeconds", "IsEnabled", "LastRunAt", "LastRunError", "Locale", "Name", "Pages", "Parse", "Query", "SortBy", "Source", "StartPage" },
                values: new object[] { 1, new DateTime(2026, 7, 17, 0, 0, 0, 0, DateTimeKind.Utc), "eg", "Cairo", new TimeSpan(0, 0, 10, 0, 0), 600, true, null, null, "ar-EG", "Books Egypt (price low to high)", 1, true, "book", "price_low_to_high", "amazon_search", 1 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScraperSearches");
        }
    }
}
