using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiliate.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TagId",
                table: "ScraperUrls",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SearchTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameAr = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchTags", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScraperUrls_TagId",
                table: "ScraperUrls",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchTags_NameEn",
                table: "SearchTags",
                column: "NameEn");

            migrationBuilder.AddForeignKey(
                name: "FK_ScraperUrls_SearchTags_TagId",
                table: "ScraperUrls",
                column: "TagId",
                principalTable: "SearchTags",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ScraperUrls_SearchTags_TagId",
                table: "ScraperUrls");

            migrationBuilder.DropTable(
                name: "SearchTags");

            migrationBuilder.DropIndex(
                name: "IX_ScraperUrls_TagId",
                table: "ScraperUrls");

            migrationBuilder.DropColumn(
                name: "TagId",
                table: "ScraperUrls");
        }
    }
}
