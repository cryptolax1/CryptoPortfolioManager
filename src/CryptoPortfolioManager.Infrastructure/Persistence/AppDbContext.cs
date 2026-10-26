using CryptoPortfolioManager.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace CryptoPortfolioManager.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();
    public DbSet<PriceUpdateLog> PriceUpdateLogs => Set<PriceUpdateLog>();
    public DbSet<PositionChangeLog> PositionChangeLogs => Set<PositionChangeLog>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WhitelistedToken> WhitelistedTokens => Set<WhitelistedToken>();

    // SQLite stores decimals as TEXT (exact), and DateTime as ISO text. Decimal columns therefore cannot be
    // used in SQL ORDER BY/SUM; do such calculations in memory. Timestamps are always normalized to UTC.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
        configurationBuilder.Properties<decimal>().HavePrecision(38, 18);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Asset>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(100);
            e.Property(a => a.Symbol).HasMaxLength(20);
            e.Property(a => a.CoinGeckoId).HasMaxLength(100);
            e.Property(a => a.ContractChain).HasConversion<string>().HasMaxLength(30);
            e.Property(a => a.ContractAddress).HasMaxLength(100);

            e.HasIndex(a => a.CoinGeckoId).IsUnique();
            e.HasIndex(a => a.Symbol);
            // A contract exists once per chain (NULLs for native coins are ignored by the filter).
            e.HasIndex(a => new { a.ContractChain, a.ContractAddress }).IsUnique()
                .HasFilter("\"ContractAddress\" IS NOT NULL");
        });

        modelBuilder.Entity<Position>(e =>
        {
            e.Property(p => p.WalletOrExchangeName).HasMaxLength(100);
            e.Property(p => p.SourceType).HasConversion<string>().HasMaxLength(30);
            e.Property(p => p.Chain).HasConversion<string>().HasMaxLength(30);
            e.Property(p => p.WalletAddress).HasMaxLength(120);
            e.Property(p => p.Notes).HasMaxLength(2000);

            // Restrict: deleting an asset must not silently delete holdings.
            e.HasOne(p => p.Asset).WithMany(a => a.Positions)
                .HasForeignKey(p => p.AssetId).IsRequired().OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(p => p.AssetId);
            e.HasIndex(p => p.WalletId);

            // Positions read from a wallet are derived data: they go away with the wallet.
            e.HasOne(p => p.Wallet).WithMany(w => w.Positions)
                .HasForeignKey(p => p.WalletId).IsRequired(false).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => new { p.Chain, p.WalletAddress }); // lookup for wallet sync jobs
            e.HasIndex(p => new { p.AssetId, p.Chain, p.WalletAddress }).IsUnique()
                .HasFilter("\"WalletAddress\" IS NOT NULL");

            // Decimals are TEXT in SQLite, so compare numerically via CAST.
            e.ToTable(t => t.HasCheckConstraint("CK_Positions_Quantity", "CAST(\"Quantity\" AS REAL) >= 0"));
        });

        modelBuilder.Entity<Wallet>(e =>
        {
            e.Property(w => w.Address).HasMaxLength(120).UseCollation("NOCASE");
            e.Property(w => w.Name).HasMaxLength(100);
            e.Property(w => w.Source).HasMaxLength(100);
            e.Property(w => w.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(w => w.LastScanError).HasMaxLength(4000);

            e.HasIndex(w => w.Address).IsUnique();
        });

        modelBuilder.Entity<WhitelistedToken>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(100);
            e.Property(t => t.Symbol).HasMaxLength(20);
            e.Property(t => t.Chain).HasConversion<string>().HasMaxLength(30);
            e.Property(t => t.ContractAddress).HasMaxLength(100).UseCollation("NOCASE");

            e.HasOne(t => t.Asset).WithMany().HasForeignKey(t => t.AssetId).IsRequired().OnDelete(DeleteBehavior.Restrict);

            // A token contract is whitelisted once per chain, and a chain has a single native coin.
            e.HasIndex(t => new { t.Chain, t.ContractAddress }).IsUnique().HasFilter("\"ContractAddress\" IS NOT NULL");
            e.HasIndex(t => t.Chain).IsUnique().HasFilter("\"ContractAddress\" IS NULL");
            e.HasIndex(t => t.AssetId);

            e.ToTable(t => t.HasCheckConstraint("CK_WhitelistedTokens_Decimals", "\"Decimals\" BETWEEN 0 AND 36"));
        });

        modelBuilder.Entity<PriceSnapshot>(e =>
        {
            e.Property(s => s.Source).HasMaxLength(50);

            e.HasOne(s => s.Asset).WithMany(a => a.PriceSnapshots)
                .HasForeignKey(s => s.AssetId).IsRequired().OnDelete(DeleteBehavior.Cascade);

            // Serves "latest price" and history queries per asset; unique prevents duplicate imports.
            e.HasIndex(s => new { s.AssetId, s.TimestampUtc, s.Source }).IsUnique();

            e.ToTable(t => t.HasCheckConstraint("CK_PriceSnapshots_PriceUsd", "CAST(\"PriceUsd\" AS REAL) >= 0"));
        });

        modelBuilder.Entity<PositionChangeLog>(e =>
        {
            e.Property(l => l.ChangeType).HasConversion<string>().HasMaxLength(30);
            e.Property(l => l.Reason).HasMaxLength(100);
            e.Property(l => l.AssetSymbol).HasMaxLength(20);
            e.Property(l => l.AssetName).HasMaxLength(100);
            e.Property(l => l.WalletOrExchangeName).HasMaxLength(100);
            e.Property(l => l.Chain).HasConversion<string>().HasMaxLength(30);
            e.Property(l => l.WalletAddress).HasMaxLength(120);

            e.HasIndex(l => l.OccurredAtUtc);
            e.HasIndex(l => new { l.AssetId, l.OccurredAtUtc });
        });

        modelBuilder.Entity<PriceUpdateLog>(e =>
        {
            e.Property(l => l.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(l => l.ErrorMessage).HasMaxLength(4000);
            e.HasIndex(l => l.StartedAtUtc);
        });
    }
}
