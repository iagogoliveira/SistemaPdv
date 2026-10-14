using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pdv.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FiscalRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerDocument",
                table: "Sales",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerName",
                table: "Sales",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cest",
                table: "SaleItem",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cfop",
                table: "SaleItem",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CofinsCst",
                table: "SaleItem",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CofinsRate",
                table: "SaleItem",
                type: "numeric(7,4)",
                precision: 7,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Csosn",
                table: "SaleItem",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IcmsCst",
                table: "SaleItem",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "IcmsRate",
                table: "SaleItem",
                type: "numeric(7,4)",
                precision: 7,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Ncm",
                table: "SaleItem",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "SaleItem",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PisCst",
                table: "SaleItem",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PisRate",
                table: "SaleItem",
                type: "numeric(7,4)",
                precision: 7,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Cest",
                table: "Products",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ncm",
                table: "Products",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "Products",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "TaxProfileId",
                table: "Products",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressCity",
                table: "Companies",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressCityIbgeCode",
                table: "Companies",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressComplement",
                table: "Companies",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressDistrict",
                table: "Companies",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressNumber",
                table: "Companies",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressStreet",
                table: "Companies",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressUf",
                table: "Companies",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressZipCode",
                table: "Companies",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultTaxProfileId",
                table: "Companies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MunicipalRegistration",
                table: "Companies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "Companies",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StateRegistration",
                table: "Companies",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxRegime",
                table: "Companies",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TradeName",
                table: "Companies",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TaxProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Cfop = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    Csosn = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    IcmsCst = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    IcmsRate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    PisCst = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    PisRate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    CofinsCst = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    CofinsRate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Products_TaxProfileId",
                table: "Products",
                column: "TaxProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_DefaultTaxProfileId",
                table: "Companies",
                column: "DefaultTaxProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxProfiles_Name",
                table: "TaxProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Companies_TaxProfiles_DefaultTaxProfileId",
                table: "Companies",
                column: "DefaultTaxProfileId",
                principalTable: "TaxProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_TaxProfiles_TaxProfileId",
                table: "Products",
                column: "TaxProfileId",
                principalTable: "TaxProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Companies_TaxProfiles_DefaultTaxProfileId",
                table: "Companies");

            migrationBuilder.DropForeignKey(
                name: "FK_Products_TaxProfiles_TaxProfileId",
                table: "Products");

            migrationBuilder.DropTable(
                name: "TaxProfiles");

            migrationBuilder.DropIndex(
                name: "IX_Products_TaxProfileId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Companies_DefaultTaxProfileId",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "CustomerDocument",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "CustomerName",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "Cest",
                table: "SaleItem");

            migrationBuilder.DropColumn(
                name: "Cfop",
                table: "SaleItem");

            migrationBuilder.DropColumn(
                name: "CofinsCst",
                table: "SaleItem");

            migrationBuilder.DropColumn(
                name: "CofinsRate",
                table: "SaleItem");

            migrationBuilder.DropColumn(
                name: "Csosn",
                table: "SaleItem");

            migrationBuilder.DropColumn(
                name: "IcmsCst",
                table: "SaleItem");

            migrationBuilder.DropColumn(
                name: "IcmsRate",
                table: "SaleItem");

            migrationBuilder.DropColumn(
                name: "Ncm",
                table: "SaleItem");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "SaleItem");

            migrationBuilder.DropColumn(
                name: "PisCst",
                table: "SaleItem");

            migrationBuilder.DropColumn(
                name: "PisRate",
                table: "SaleItem");

            migrationBuilder.DropColumn(
                name: "Cest",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Ncm",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "TaxProfileId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "AddressCity",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "AddressCityIbgeCode",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "AddressComplement",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "AddressDistrict",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "AddressNumber",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "AddressStreet",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "AddressUf",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "AddressZipCode",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "DefaultTaxProfileId",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "MunicipalRegistration",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "StateRegistration",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "TaxRegime",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "TradeName",
                table: "Companies");
        }
    }
}
