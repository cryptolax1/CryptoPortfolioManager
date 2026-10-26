using CryptoPortfolioManager.Core;
using CryptoPortfolioManager.Infrastructure;
using CryptoPortfolioManager.Infrastructure.Blockchain;
using CryptoPortfolioManager.Infrastructure.CoinGecko;
using CryptoPortfolioManager.Infrastructure.Persistence;
using CryptoPortfolioManager.Web;
using CryptoPortfolioManager.Web.Components;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Database selection: ConnectionStrings:Default wins; otherwise Database:UseTestDatabase picks the pre-filled test
// database (created by tools\CryptoPortfolioManager.TestDb) and the real database is used by default.
// The application never inserts test data. On first start it only adds the default wallets and token whitelist.
var useTestDatabase = builder.Configuration.GetValue<bool>("Database:UseTestDatabase");
var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
{
    var path = DatabasePaths.Default(useTestDatabase);
    if (useTestDatabase && !File.Exists(path))
        throw new InvalidOperationException(
            $"Test database not found at '{path}'. Create it with: dotnet run --project tools\\CryptoPortfolioManager.TestDb");

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    connectionString = DatabasePaths.ConnectionString(path);
}

builder.Services.AddSingleton(new DatabaseInfo(useTestDatabase && string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Default"))));
builder.Services.AddCore();
builder.Services.AddInfrastructure(connectionString);
builder.Services.AddCoinGeckoPriceProvider(builder.Configuration);
builder.Services.AddBlockchainBalanceReaders(builder.Configuration);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
    await DefaultReferenceData.EnsureAsync(db);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
