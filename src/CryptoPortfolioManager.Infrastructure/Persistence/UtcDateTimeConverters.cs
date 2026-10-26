using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CryptoPortfolioManager.Infrastructure.Persistence;

public class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

public class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v == null ? v : (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()),
    v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
