using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPortfolioManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionChangeLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PositionChangeLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChangeType = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    AssetSymbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AssetName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    WalletOrExchangeName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Chain = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    WalletAddress = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PreviousQuantity = table.Column<decimal>(type: "TEXT", precision: 38, scale: 18, nullable: true),
                    NewQuantity = table.Column<decimal>(type: "TEXT", precision: 38, scale: 18, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PositionChangeLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PositionChangeLogs_AssetId_OccurredAtUtc",
                table: "PositionChangeLogs",
                columns: new[] { "AssetId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PositionChangeLogs_OccurredAtUtc",
                table: "PositionChangeLogs",
                column: "OccurredAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PositionChangeLogs");
        }
    }
}
