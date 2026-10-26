using CryptoPortfolioManager.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CryptoPortfolioManager.Tests;

/// <summary>In-memory SQLite database built from the real migrations, so tests cover the actual schema.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

    private TestDatabase()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
    }

    public static async Task<TestDatabase> CreateAsync(bool seed = false)
    {
        var testDb = new TestDatabase();
        await using var db = testDb.CreateContext();
        await db.Database.MigrateAsync();
        if (seed) await SampleDataSeeder.SeedAsync(db);
        return testDb;
    }

    public AppDbContext CreateContext() => new(_options);

    public IDbContextFactory<AppDbContext> CreateFactory() => new Factory(this);

    private sealed class Factory(TestDatabase db) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => db.CreateContext();
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
