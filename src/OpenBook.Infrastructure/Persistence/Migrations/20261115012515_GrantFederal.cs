using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GrantFederal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssistanceListing",
                table: "Grants",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FederalAgency",
                table: "Grants",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "IndirectRateTenThousandths",
                table: "Grants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "PassThroughEntity",
                table: "Grants",
                type: "TEXT",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssistanceListing",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "FederalAgency",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "IndirectRateTenThousandths",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "PassThroughEntity",
                table: "Grants");
        }
    }
}
