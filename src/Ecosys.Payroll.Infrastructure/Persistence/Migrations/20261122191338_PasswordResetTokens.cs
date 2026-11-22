using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecosys.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PasswordResetTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "password_reset_expires_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "password_reset_token_hash",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_password_reset_token_hash",
                table: "users",
                column: "password_reset_token_hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_password_reset_token_hash",
                table: "users");

            migrationBuilder.DropColumn(
                name: "password_reset_expires_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "password_reset_token_hash",
                table: "users");
        }
    }
}
