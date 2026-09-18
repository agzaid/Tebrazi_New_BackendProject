using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Tebrazi.Infrastructure.Shared.Persistence;

internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

internal sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime()) : null,
    v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : null);
