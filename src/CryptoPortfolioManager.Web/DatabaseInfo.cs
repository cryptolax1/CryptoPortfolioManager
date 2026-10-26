namespace CryptoPortfolioManager.Web;

/// <summary>Tells the UI which database is in use, so test data is never mistaken for real data.</summary>
public sealed record DatabaseInfo(bool IsTest);
