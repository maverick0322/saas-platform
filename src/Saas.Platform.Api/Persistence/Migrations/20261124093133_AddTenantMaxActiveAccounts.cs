using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saas.Platform.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantMaxActiveAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "max_active_accounts",
                schema: "platform",
                table: "tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_max_active_accounts_positive",
                schema: "platform",
                table: "tenants",
                sql: "max_active_accounts IS NULL OR max_active_accounts >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_max_active_accounts_positive",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "max_active_accounts",
                schema: "platform",
                table: "tenants");
        }
    }
}
