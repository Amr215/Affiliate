using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiliate.Migrations
{
    /// <inheritdoc />
    public partial class AddNoonShortLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'[NoonShortLinks]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [NoonShortLinks] (
                        [Id] int NOT NULL IDENTITY,
                        [Code] nvarchar(16) NOT NULL,
                        [DestinationUrl] nvarchar(4000) NOT NULL,
                        [CreatedAt] datetime2 NOT NULL,
                        CONSTRAINT [PK_NoonShortLinks] PRIMARY KEY ([Id])
                    );
                    CREATE UNIQUE INDEX [IX_NoonShortLinks_Code] ON [NoonShortLinks] ([Code]);
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoonShortLinks");
        }
    }
}
