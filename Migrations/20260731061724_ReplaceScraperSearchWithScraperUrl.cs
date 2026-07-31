using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiliate.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceScraperSearchWithScraperUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OxylabsRequestLogs_ScraperSearches_ScraperSearchId",
                table: "OxylabsRequestLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_Products_ScraperSearches_ScraperSearchId",
                table: "Products");

            migrationBuilder.CreateTable(
                name: "ScraperUrls",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Domain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    StartPage = table.Column<int>(type: "int", nullable: false),
                    IntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRunError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScraperUrls", x => x.Id);
                });

            // Preserve IDs so Product / OxylabsRequestLog FKs stay valid.
            // Existing Query rows become placeholder amazon search URLs — replace with real listing URLs in admin.
            migrationBuilder.Sql("""
                SET IDENTITY_INSERT [ScraperUrls] ON;

                INSERT INTO [ScraperUrls] (
                    [Id], [Name], [Domain], [Url], [StartPage], [IntervalSeconds],
                    [IsEnabled], [LastRunAt], [LastRunError], [CreatedAt])
                SELECT
                    [Id],
                    [Name],
                    [Domain],
                    N'https://www.amazon.' + [Domain] + N'/s?k=' + REPLACE(REPLACE([Query], N' ', N'+'), N'&', N'%26'),
                    [StartPage],
                    [IntervalSeconds],
                    [IsEnabled],
                    [LastRunAt],
                    [LastRunError],
                    [CreatedAt]
                FROM [ScraperSearches];

                SET IDENTITY_INSERT [ScraperUrls] OFF;
                """);

            migrationBuilder.DropTable(
                name: "ScraperSearches");

            migrationBuilder.RenameColumn(
                name: "ScraperSearchId",
                table: "Products",
                newName: "ScraperUrlId");

            migrationBuilder.RenameIndex(
                name: "IX_Products_ScraperSearchId",
                table: "Products",
                newName: "IX_Products_ScraperUrlId");

            migrationBuilder.RenameColumn(
                name: "ScraperSearchId",
                table: "OxylabsRequestLogs",
                newName: "ScraperUrlId");

            migrationBuilder.RenameIndex(
                name: "IX_OxylabsRequestLogs_ScraperSearchId_RequestedAt",
                table: "OxylabsRequestLogs",
                newName: "IX_OxylabsRequestLogs_ScraperUrlId_RequestedAt");

            migrationBuilder.RenameIndex(
                name: "IX_OxylabsRequestLogs_ScraperSearchId",
                table: "OxylabsRequestLogs",
                newName: "IX_OxylabsRequestLogs_ScraperUrlId");

            migrationBuilder.AddForeignKey(
                name: "FK_OxylabsRequestLogs_ScraperUrls_ScraperUrlId",
                table: "OxylabsRequestLogs",
                column: "ScraperUrlId",
                principalTable: "ScraperUrls",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_ScraperUrls_ScraperUrlId",
                table: "Products",
                column: "ScraperUrlId",
                principalTable: "ScraperUrls",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OxylabsRequestLogs_ScraperUrls_ScraperUrlId",
                table: "OxylabsRequestLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_Products_ScraperUrls_ScraperUrlId",
                table: "Products");

            migrationBuilder.CreateTable(
                name: "ScraperSearches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CategoryId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CheckEmptyGeo = table.Column<bool>(type: "bit", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Domain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ForceCookies = table.Column<bool>(type: "bit", nullable: false),
                    ForceHeaders = table.Column<bool>(type: "bit", nullable: false),
                    GeoLocation = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    HcPolicy = table.Column<bool>(type: "bit", nullable: false),
                    IntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRunError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Locale = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MaxPrice = table.Column<int>(type: "int", nullable: true),
                    MerchantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MinPrice = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Parse = table.Column<bool>(type: "bit", nullable: false),
                    Query = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Refinements = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SafeSearch = table.Column<bool>(type: "bit", nullable: false),
                    SortBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StartPage = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScraperSearches", x => x.Id);
                });

            migrationBuilder.Sql("""
                SET IDENTITY_INSERT [ScraperSearches] ON;

                INSERT INTO [ScraperSearches] (
                    [Id], [Name], [Source], [Domain], [Query], [Locale], [StartPage], [Parse],
                    [ForceHeaders], [ForceCookies], [HcPolicy], [SafeSearch],
                    [IntervalSeconds], [IsEnabled], [LastRunAt], [LastRunError], [CreatedAt])
                SELECT
                    [Id],
                    [Name],
                    N'amazon_search',
                    [Domain],
                    LEFT([Url], 500),
                    N'en-AE',
                    [StartPage],
                    1,
                    0,
                    0,
                    1,
                    1,
                    [IntervalSeconds],
                    [IsEnabled],
                    [LastRunAt],
                    [LastRunError],
                    [CreatedAt]
                FROM [ScraperUrls];

                SET IDENTITY_INSERT [ScraperSearches] OFF;
                """);

            migrationBuilder.DropTable(
                name: "ScraperUrls");

            migrationBuilder.RenameColumn(
                name: "ScraperUrlId",
                table: "Products",
                newName: "ScraperSearchId");

            migrationBuilder.RenameIndex(
                name: "IX_Products_ScraperUrlId",
                table: "Products",
                newName: "IX_Products_ScraperSearchId");

            migrationBuilder.RenameColumn(
                name: "ScraperUrlId",
                table: "OxylabsRequestLogs",
                newName: "ScraperSearchId");

            migrationBuilder.RenameIndex(
                name: "IX_OxylabsRequestLogs_ScraperUrlId_RequestedAt",
                table: "OxylabsRequestLogs",
                newName: "IX_OxylabsRequestLogs_ScraperSearchId_RequestedAt");

            migrationBuilder.RenameIndex(
                name: "IX_OxylabsRequestLogs_ScraperUrlId",
                table: "OxylabsRequestLogs",
                newName: "IX_OxylabsRequestLogs_ScraperSearchId");

            migrationBuilder.AddForeignKey(
                name: "FK_OxylabsRequestLogs_ScraperSearches_ScraperSearchId",
                table: "OxylabsRequestLogs",
                column: "ScraperSearchId",
                principalTable: "ScraperSearches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_ScraperSearches_ScraperSearchId",
                table: "Products",
                column: "ScraperSearchId",
                principalTable: "ScraperSearches",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
