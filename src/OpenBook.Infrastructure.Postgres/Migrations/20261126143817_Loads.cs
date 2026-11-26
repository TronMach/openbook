using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Loads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Loads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MilesTenThousandths = table.Column<long>(type: "bigint", nullable: true),
                    Commodity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    WeightTenThousandths = table.Column<long>(type: "bigint", nullable: true),
                    Memo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Loads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Loads_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dispatches",
                columns: table => new
                {
                    Number = table.Column<int>(type: "integer", nullable: false),
                    LoadId = table.Column<Guid>(type: "uuid", nullable: false),
                    TruckId = table.Column<Guid>(type: "uuid", nullable: false),
                    Driver = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LoadedMilesTenThousandths = table.Column<long>(type: "bigint", nullable: true),
                    EmptyMilesTenThousandths = table.Column<long>(type: "bigint", nullable: true),
                    ShareTenThousandths = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dispatches", x => new { x.LoadId, x.Number });
                    table.ForeignKey(
                        name: "FK_Dispatches_Loads_LoadId",
                        column: x => x.LoadId,
                        principalTable: "Loads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dispatches_Trucks_TruckId",
                        column: x => x.TruckId,
                        principalTable: "Trucks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LoadCharges",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    LoadId = table.Column<Guid>(type: "uuid", nullable: false),
                    Charge = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    QuantityTenThousandths = table.Column<long>(type: "bigint", nullable: false),
                    RateTenThousandths = table.Column<long>(type: "bigint", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoadCharges", x => new { x.LoadId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_LoadCharges_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoadCharges_Loads_LoadId",
                        column: x => x.LoadId,
                        principalTable: "Loads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoadStops",
                columns: table => new
                {
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    LoadId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Place = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    AppointmentFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AppointmentTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Arrived = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Departed = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoadStops", x => new { x.LoadId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_LoadStops_Loads_LoadId",
                        column: x => x.LoadId,
                        principalTable: "Loads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dispatches_TruckId",
                table: "Dispatches",
                column: "TruckId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadCharges_ItemId",
                table: "LoadCharges",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Loads_CustomerId",
                table: "Loads",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Loads_Number",
                table: "Loads",
                column: "Number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dispatches");

            migrationBuilder.DropTable(
                name: "LoadCharges");

            migrationBuilder.DropTable(
                name: "LoadStops");

            migrationBuilder.DropTable(
                name: "Loads");
        }
    }
}
