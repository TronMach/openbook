using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountExcluded1099 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Excluded1099",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Native, not EF's DropColumn: rebuilding Accounts would lose IX_Accounts_SiblingName (raw SQL, not in the model).
            migrationBuilder.Sql("ALTER TABLE \"Accounts\" DROP COLUMN \"Excluded1099\";");
        }
    }
}
