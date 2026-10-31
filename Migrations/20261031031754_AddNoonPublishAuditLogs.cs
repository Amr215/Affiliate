using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiliate.Migrations
{
    /// <inheritdoc />
    public partial class AddNoonPublishAuditLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Table may already exist from an earlier local Apply (history row AddNoonPublish
            // without matching source files). Create only when missing.
            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'[NoonPublishAuditLogs]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [NoonPublishAuditLogs] (
                        [Id] bigint NOT NULL IDENTITY,
                        [CorrelationId] uniqueidentifier NOT NULL,
                        [Sku] nvarchar(64) NOT NULL,
                        [EventType] int NOT NULL,
                        [OccurredAt] datetime2 NOT NULL,
                        [Route] nvarchar(64) NULL,
                        [ProxyHost] nvarchar(128) NULL,
                        [ProxyPort] int NULL,
                        [PageKind] nvarchar(8) NULL,
                        [Success] bit NULL,
                        [DurationMs] int NULL,
                        [Detail] nvarchar(500) NULL,
                        [ChatId] nvarchar(64) NULL,
                        [DraftToken] nvarchar(16) NULL,
                        CONSTRAINT [PK_NoonPublishAuditLogs] PRIMARY KEY ([Id])
                    );
                    CREATE INDEX [IX_NoonPublishAuditLogs_CorrelationId] ON [NoonPublishAuditLogs] ([CorrelationId]);
                    CREATE INDEX [IX_NoonPublishAuditLogs_OccurredAt] ON [NoonPublishAuditLogs] ([OccurredAt]);
                    CREATE INDEX [IX_NoonPublishAuditLogs_Sku_OccurredAt] ON [NoonPublishAuditLogs] ([Sku], [OccurredAt]);
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoonPublishAuditLogs");
        }
    }
}
