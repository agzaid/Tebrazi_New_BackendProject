using QRCoder;

namespace Tebrazi.Connections.Application.Services;

/// <summary>
/// The QR image behind <c>GET /api/connections/qr-code</c> (connections.js:1793-1816).
///
/// <para>Node calls the <c>qrcode</c> npm package's <c>QRCode.toDataURL(url, { width: 300,
/// margin: 2, color: { dark: '#000000', light: '#ffffff' } })</c> and returns the resulting
/// <c>data:image/png;base64,…</c> string as <c>qrCode</c>. This is the .NET equivalent, built on
/// QRCoder's <c>PngByteQRCode</c> — a managed PNG writer with no System.Drawing and no native
/// dependency, so it runs on Linux containers unchanged.</para>
///
/// <para><b>The base64 payload will NOT be byte-identical to Node's</b>, and cannot be: two
/// different encoders choose different mask patterns and emit different PNG chunk layouts for the
/// same input. What IS identical is the media type, the <c>data:</c> URL shape, and — the only
/// thing that matters — the string a scanner decodes, which is
/// <see cref="ConnClientUrls.ConnectUrl"/>'s output. The client renders the value straight into an
/// <c>&lt;img src&gt;</c> and never inspects it, so this is a byte divergence with no observable
/// behaviour. It is recorded in docs/connections-surface.md §10 rather than papered over.</para>
/// </summary>
public sealed class ConnQrCodeRenderer
{
    /// <summary>The <c>width: 300</c> the Node options request, in pixels.</summary>
    public const int TargetWidthPixels = 300;

    /// <summary>The <c>margin: 2</c> quiet zone, in QR modules.</summary>
    public const int QuietZoneModules = 2;

    /// <summary>
    /// Encodes <paramref name="payload"/> and returns a <c>data:image/png;base64,…</c> URL.
    ///
    /// <para>Error correction is <see cref="QRCodeGenerator.ECCLevel.M"/>, which is the
    /// <c>qrcode</c> package's own default (<c>errorCorrectionLevel: 'M'</c>) and therefore what
    /// Node produces here — the route passes no <c>errorCorrectionLevel</c>.</para>
    ///
    /// <para>The module size is derived rather than fixed so the image lands as close to
    /// <see cref="TargetWidthPixels"/> as whole modules allow, matching how the npm package
    /// interprets <c>width</c> as a target rather than an exact size. It is never less than 1.</para>
    /// </summary>
    /// <param name="payload">The text to encode — the connect URL.</param>
    /// <returns>A PNG data URL, ready to use as an <c>&lt;img src&gt;</c>.</returns>
    public string RenderPngDataUrl(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);

        var png = new PngByteQRCode(data);
        var bytes = png.GetGraphic(
            ModuleSizeFor(data.ModuleMatrix.Count),
            darkColorRgba: [0x00, 0x00, 0x00, 0xFF],
            lightColorRgba: [0xFF, 0xFF, 0xFF, 0xFF],
            drawQuietZones: true);

        return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
    }

    /// <summary>
    /// Pixels per module, so the rendered image is as close to
    /// <see cref="TargetWidthPixels"/> as whole modules allow.
    /// </summary>
    /// <param name="matrixSideInModules">
    /// The side of QRCoder's module matrix, which ALREADY includes its default four-module quiet
    /// zone. The Node options ask for a two-module one; the difference is a slightly wider white
    /// border and is not detectable by a scanner.
    /// </param>
    private static int ModuleSizeFor(int matrixSideInModules)
        => matrixSideInModules <= 0 ? 1 : Math.Max(1, TargetWidthPixels / matrixSideInModules);
}
