namespace CryptoPortfolioManager.Infrastructure.Persistence;

/// <summary>Default SQLite file locations: one database for real data, one for test data.</summary>
public static class DatabasePaths
{
    public const string RealFileName = "portfolio.db";
    public const string TestFileName = "portfolio.test.db";

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CryptoPortfolioManager");

    public static string Default(bool test) =>
        Path.Combine(DefaultDirectory, test ? TestFileName : RealFileName);

    public static string ConnectionString(string path) => $"Data Source={path}";
}
