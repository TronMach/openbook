using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class RetainageItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRetainage",
                table: "Items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Until now the item was known by its name. One that posts to an income account isn't the one OpenBook made.
            migrationBuilder.Sql("""
                UPDATE "Items" SET "IsRetainage" = TRUE
                WHERE lower("Name") = 'retainage' AND "Type" = 'Service'
                  AND "IncomeAccountId" IN (SELECT "Id" FROM "Accounts" WHERE "Type" = 'OtherCurrentAsset');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Items\" DROP COLUMN \"IsRetainage\";");
        }
    }
}
