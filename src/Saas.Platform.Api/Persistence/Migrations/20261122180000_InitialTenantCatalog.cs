using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saas.Platform.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialTenantCatalog : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "platform");

        migrationBuilder.CreateTable(
            name: "tenants",
            schema: "platform",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                public_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                contact_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                concurrency_stamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_tenants", x => x.id);
                table.CheckConstraint("ck_tenants_contact_email_not_empty", "char_length(btrim(contact_email)) >= 3");
                table.CheckConstraint("ck_tenants_name_length", "char_length(btrim(name)) >= 2");
                table.CheckConstraint("ck_tenants_public_id_length", "char_length(btrim(public_id)) >= 3");
            });

        migrationBuilder.CreateIndex(
            name: "ux_tenants_public_id",
            schema: "platform",
            table: "tenants",
            column: "public_id",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "tenants",
            schema: "platform");
    }
}
