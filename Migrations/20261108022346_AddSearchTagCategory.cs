using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiliate.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchTagCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "SearchTags",
                type: "int",
                nullable: false,
                defaultValue: 99);

            migrationBuilder.CreateIndex(
                name: "IX_SearchTags_Category",
                table: "SearchTags",
                column: "Category");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SearchTags_Category",
                table: "SearchTags");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "SearchTags");
        }
    }
}
