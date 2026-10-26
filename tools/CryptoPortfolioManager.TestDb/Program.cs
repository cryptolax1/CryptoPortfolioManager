using CryptoPortfolioManager.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// Creates the pre-filled test database. The application itself never inserts data.
//   dotnet run --project tools\CryptoPortfolioManager.TestDb [-- [--reset] [--with-sample-prices] [--path <file>]]
var reset = args.Contains("--reset");
var withPrices = args.Contains("--with-sample-prices");
var pathIndex = Array.IndexOf(args, "--path");
var path = pathIndex >= 0 && pathIndex + 1 < args.Length
    ? Path.GetFullPath(args[pathIndex + 1])
    : DatabasePaths.Default(test: true);

if (Path.GetFileName(path).Equals(DatabasePaths.RealFileName, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Refusing to touch '{path}': that is the name of the real database.");
    return 1;
}

if (File.Exists(path) && !reset)
{
    Console.WriteLine($"Test database already exists, left unchanged: {path}");
    Console.WriteLine("Use --reset to recreate it with fresh test data.");
    return 0;
}

Directory.CreateDirectory(Path.GetDirectoryName(path)!);
foreach (var file in new[] { path, path + "-wal", path + "-shm" })
    if (File.Exists(file)) File.Delete(file);

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlite(DatabasePaths.ConnectionString(path))
    .Options;

await using (var db = new AppDbContext(options))
{
    await db.Database.MigrateAsync();
    await SampleDataSeeder.SeedAsync(db, includePrices: withPrices);
    await DefaultReferenceData.EnsureAsync(db);

    Console.WriteLine($"Test database created: {path}");
    Console.WriteLine($"  Assets:    {await db.Assets.CountAsync()}");
    Console.WriteLine($"  Positions: {await db.Positions.CountAsync()}");
    Console.WriteLine($"  Wallets:   {await db.Wallets.CountAsync()} (scan with the Re-scan wallets button)");
    Console.WriteLine($"  Whitelist: {await db.WhitelistedTokens.CountAsync()} tokens");
    Console.WriteLine($"  Prices:    {await db.PriceSnapshots.CountAsync()}" + (withPrices ? " (fictitious)" : " (none; fetch them with an API sync)"));
}

SqliteConnection.ClearAllPools();
return 0;
