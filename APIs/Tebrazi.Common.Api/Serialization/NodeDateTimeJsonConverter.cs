using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tebrazi.Common.Api.Serialization;

/// <summary>
/// Writes every <see cref="DateTime"/> the way <c>JSON.stringify</c> writes a JavaScript
/// <c>Date</c>: <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>, always UTC, always exactly three fractional
/// digits.
///
/// Express reaches this shape without being asked. <c>res.json</c> calls
/// <c>JSON.stringify</c>, which calls <c>Date.prototype.toJSON</c> and therefore
/// <c>toISOString</c>, whose output is fixed by ECMA-262 at 24 characters with a literal
/// <c>Z</c>. <c>System.Text.Json</c>'s default is round-trippable rather than identical: it
/// omits the fractional part when it is zero (<c>2026-09-10T00:00:00Z</c>) and emits up to
/// seven digits when it is not (<c>2026-09-10T12:34:56.1234567Z</c>). Both differ from Node on
/// the wire, and timestamps appear on nearly every response this backend serves.
///
/// Sub-millisecond precision is truncated, not rounded, which is also what the Node data looks
/// like: a Prisma <c>DateTime</c> carries milliseconds, while a <c>datetime2</c> column written
/// by <c>DateTime.UtcNow</c> carries 100-nanosecond ticks that no Node row would ever hold.
/// </summary>
/// <remarks>
/// <see cref="DateTimeKind.Unspecified"/> is treated as already-UTC rather than converted.
/// Every instant in the model is UTC — <c>BaseDbContext</c> stamps
/// <see cref="DateTimeKind.Utc"/> on read — and calling
/// <see cref="DateTime.ToUniversalTime"/> on a value the provider handed back as Unspecified
/// would shift it by the server's offset.
///
/// Reading is left to <see cref="Utf8JsonReader.GetDateTime"/> so request binding keeps the
/// exact ISO-8601 tolerance, and the exact failure, it had before this converter existed.
/// </remarks>
public sealed class NodeDateTimeJsonConverter : JsonConverter<DateTime>
{
    private const string NodeIsoFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>"2026-09-10T00:00:00.000Z" — fixed width, so the buffer never grows.</summary>
    private const int NodeIsoLength = 24;

    /// <inheritdoc />
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTime();

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;

        Span<char> buffer = stackalloc char[NodeIsoLength];
        _ = utc.TryFormat(buffer, out var written, NodeIsoFormat, CultureInfo.InvariantCulture);

        writer.WriteStringValue(buffer[..written]);
    }
}
