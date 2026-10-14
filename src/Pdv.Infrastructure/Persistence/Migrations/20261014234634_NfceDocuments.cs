using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pdv.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NfceDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FiscalDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    Model = table.Column<int>(type: "integer", nullable: false),
                    Environment = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Series = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<long>(type: "bigint", nullable: false),
                    EmissionType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AccessKey = table.Column<string>(type: "character varying(44)", maxLength: 44, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AuthorizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Protocol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    StatusMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ContingencyReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SignedXml = table.Column<string>(type: "text", nullable: true),
                    AuthorizedXml = table.Column<string>(type: "text", nullable: true),
                    QrCodeUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ConsultationUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancellationProtocol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FiscalDocuments_Sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "Sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FiscalDocuments_Stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FiscalSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    Environment = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Model = table.Column<int>(type: "integer", nullable: false),
                    Series = table.Column<int>(type: "integer", nullable: false),
                    NextNumber = table.Column<long>(type: "bigint", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalSeries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FiscalSeries_Stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_AccessKey",
                table: "FiscalDocuments",
                column: "AccessKey");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_SaleId",
                table: "FiscalDocuments",
                column: "SaleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_Status",
                table: "FiscalDocuments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDocuments_StoreId_Environment_Model_Series_Number",
                table: "FiscalDocuments",
                columns: new[] { "StoreId", "Environment", "Model", "Series", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FiscalSeries_StoreId_Environment_Model_Series",
                table: "FiscalSeries",
                columns: new[] { "StoreId", "Environment", "Model", "Series" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FiscalDocuments");

            migrationBuilder.DropTable(
                name: "FiscalSeries");
        }
    }
}
