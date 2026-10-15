using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pdv.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Stage5Server : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessKeyHash",
                table: "Terminals",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppVersion",
                table: "Terminals",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "Terminals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PairedAt",
                table: "Terminals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Terminals_AccessKeyHash",
                table: "Terminals",
                column: "AccessKeyHash",
                unique: true,
                filter: "\"AccessKeyHash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Terminals_AccessKeyHash",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "AccessKeyHash",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "AppVersion",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "PairedAt",
                table: "Terminals");
        }
    }
}
