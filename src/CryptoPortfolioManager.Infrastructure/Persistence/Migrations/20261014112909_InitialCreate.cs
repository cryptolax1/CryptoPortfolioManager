using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPortfolioManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Assets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CoinGeckoId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ContractChain = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    ContractAddress = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TokenDecimals = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceUpdateLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceUpdateLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Positions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    WalletOrExchangeName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Chain = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    WalletAddress = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 38, scale: 18, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Positions", x => x.Id);
                    table.CheckConstraint("CK_Positions_Quantity", "CAST(\"Quantity\" AS REAL) >= 0");
                    table.ForeignKey(
                        name: "FK_Positions_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PriceSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    PriceUsd = table.Column<decimal>(type: "TEXT", precision: 38, scale: 18, nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceSnapshots", x => x.Id);
                    table.CheckConstraint("CK_PriceSnapshots_PriceUsd", "CAST(\"PriceUsd\" AS REAL) >= 0");
                    table.ForeignKey(
                        name: "FK_PriceSnapshots_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_CoinGeckoId",
                table: "Assets",
                column: "CoinGeckoId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assets_ContractChain_ContractAddress",
                table: "Assets",
                columns: new[] { "ContractChain", "ContractAddress" },
                unique: true,
                filter: "\"ContractAddress\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_Symbol",
                table: "Assets",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_AssetId",
                table: "Positions",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_AssetId_Chain_WalletAddress",
                table: "Positions",
                columns: new[] { "AssetId", "Chain", "WalletAddress" },
                unique: true,
                filter: "\"WalletAddress\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_Chain_WalletAddress",
                table: "Positions",
                columns: new[] { "Chain", "WalletAddress" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceSnapshots_AssetId_TimestampUtc_Source",
                table: "PriceSnapshots",
                columns: new[] { "AssetId", "TimestampUtc", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceUpdateLogs_StartedAtUtc",
                table: "PriceUpdateLogs",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Positions");

            migrationBuilder.DropTable(
                name: "PriceSnapshots");

            migrationBuilder.DropTable(
                name: "PriceUpdateLogs");

            migrationBuilder.DropTable(
                name: "Assets");
        }
    }
}
