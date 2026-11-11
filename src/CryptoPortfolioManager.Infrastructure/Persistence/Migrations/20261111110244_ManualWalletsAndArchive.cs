using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPortfolioManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualWalletsAndArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Wallets_Address",
                table: "Wallets");

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "Wallets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastQuantityChangedAtUtc",
                table: "Positions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WalletId",
                table: "PositionChangeLogs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_Address",
                table: "Wallets",
                column: "Address",
                unique: true,
                filter: "\"Address\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_WalletId_AssetId",
                table: "Positions",
                columns: new[] { "WalletId", "AssetId" },
                unique: true,
                filter: "\"SourceType\" = 'Manual' AND \"WalletId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Wallets_Address",
                table: "Wallets");

            migrationBuilder.DropIndex(
                name: "IX_Positions_WalletId_AssetId",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "LastQuantityChangedAtUtc",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "WalletId",
                table: "PositionChangeLogs");

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_Address",
                table: "Wallets",
                column: "Address",
                unique: true);
        }
    }
}
