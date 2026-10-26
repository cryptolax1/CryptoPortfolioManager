using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPortfolioManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletsAndWhitelist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WalletId",
                table: "Positions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Wallets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "NOCASE"),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LastScannedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastScanError = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wallets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WhitelistedTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Chain = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    ContractAddress = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true, collation: "NOCASE"),
                    Decimals = table.Column<int>(type: "INTEGER", nullable: false),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhitelistedTokens", x => x.Id);
                    table.CheckConstraint("CK_WhitelistedTokens_Decimals", "\"Decimals\" BETWEEN 0 AND 36");
                    table.ForeignKey(
                        name: "FK_WhitelistedTokens_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Positions_WalletId",
                table: "Positions",
                column: "WalletId");

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_Address",
                table: "Wallets",
                column: "Address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WhitelistedTokens_AssetId",
                table: "WhitelistedTokens",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_WhitelistedTokens_Chain",
                table: "WhitelistedTokens",
                column: "Chain",
                unique: true,
                filter: "\"ContractAddress\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WhitelistedTokens_Chain_ContractAddress",
                table: "WhitelistedTokens",
                columns: new[] { "Chain", "ContractAddress" },
                unique: true,
                filter: "\"ContractAddress\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Positions_Wallets_WalletId",
                table: "Positions",
                column: "WalletId",
                principalTable: "Wallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Positions_Wallets_WalletId",
                table: "Positions");

            migrationBuilder.DropTable(
                name: "Wallets");

            migrationBuilder.DropTable(
                name: "WhitelistedTokens");

            migrationBuilder.DropIndex(
                name: "IX_Positions_WalletId",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "WalletId",
                table: "Positions");
        }
    }
}
