using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Projects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsBillable",
                table: "BillableExpenses",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "MileageRates",
                columns: table => new
                {
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    RateTenThousandths = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MileageRates", x => x.Year);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    BudgetRevenueMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    BudgetCostMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    BudgetHoursTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Workers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CostRateTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    BillRateTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Workers_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MileageTrips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    MilesTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    WorkerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Vehicle = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    FromPlace = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ToPlace = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Billable = table.Column<bool>(type: "INTEGER", nullable: false),
                    BillRateTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MileageTrips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MileageTrips_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MileageTrips_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MileageTrips_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TimeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    HoursTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Billable = table.Column<bool>(type: "INTEGER", nullable: false),
                    BillRateTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    CostRateTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeEntries_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimeEntries_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimeEntries_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimeEntries_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MileageTrips_CustomerId",
                table: "MileageTrips",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_MileageTrips_Date",
                table: "MileageTrips",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_MileageTrips_InvoiceId",
                table: "MileageTrips",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_MileageTrips_WorkerId",
                table: "MileageTrips",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_CustomerId",
                table: "Projects",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_CustomerId",
                table: "TimeEntries",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_InvoiceId",
                table: "TimeEntries",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_ItemId",
                table: "TimeEntries",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_WorkerId_Date",
                table: "TimeEntries",
                columns: new[] { "WorkerId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Workers_VendorId",
                table: "Workers",
                column: "VendorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MileageRates");

            migrationBuilder.DropTable(
                name: "MileageTrips");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "TimeEntries");

            migrationBuilder.DropTable(
                name: "Workers");

            migrationBuilder.DropColumn(
                name: "IsBillable",
                table: "BillableExpenses");
        }
    }
}
