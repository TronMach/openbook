using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InvitePermissionSet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PermissionSetId",
                table: "UserInvites",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserInvites_PermissionSetId",
                table: "UserInvites",
                column: "PermissionSetId");

            migrationBuilder.AddForeignKey(
                name: "FK_UserInvites_PermissionSets_PermissionSetId",
                table: "UserInvites",
                column: "PermissionSetId",
                principalTable: "PermissionSets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserInvites_PermissionSets_PermissionSetId",
                table: "UserInvites");

            migrationBuilder.DropIndex(
                name: "IX_UserInvites_PermissionSetId",
                table: "UserInvites");

            migrationBuilder.DropColumn(
                name: "PermissionSetId",
                table: "UserInvites");
        }
    }
}
