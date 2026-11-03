using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class UploadFileHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileHash",
                table: "ReceiptCaptures",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileHash",
                table: "CapturedDocuments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptCaptures_FileHash",
                table: "ReceiptCaptures",
                column: "FileHash");

            migrationBuilder.CreateIndex(
                name: "IX_CapturedDocuments_FileHash",
                table: "CapturedDocuments",
                column: "FileHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReceiptCaptures_FileHash",
                table: "ReceiptCaptures");

            migrationBuilder.DropIndex(
                name: "IX_CapturedDocuments_FileHash",
                table: "CapturedDocuments");

            migrationBuilder.DropColumn(
                name: "FileHash",
                table: "ReceiptCaptures");

            migrationBuilder.DropColumn(
                name: "FileHash",
                table: "CapturedDocuments");
        }
    }
}
