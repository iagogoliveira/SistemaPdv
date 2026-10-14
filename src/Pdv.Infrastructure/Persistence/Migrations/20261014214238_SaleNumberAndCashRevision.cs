using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pdv.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SaleNumberAndCashRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "SaleNumbers");

            migrationBuilder.AddColumn<long>(
                name: "Number",
                table: "Sales",
                type: "bigint",
                nullable: false,
                defaultValueSql: "NEXT VALUE FOR [SaleNumbers]");

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "CashSessions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SaleCount",
                table: "CashSessions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Sales_Number",
                table: "Sales",
                column: "Number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sales_Number",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "CashSessions");

            migrationBuilder.DropColumn(
                name: "SaleCount",
                table: "CashSessions");

            migrationBuilder.DropSequence(
                name: "SaleNumbers");
        }
    }
}
