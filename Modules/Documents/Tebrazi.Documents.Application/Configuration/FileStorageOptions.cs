namespace Tebrazi.Documents.Application.Configuration;

/// <summary>Bound from the <c>FileStorage</c> configuration section.</summary>
public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    /// <summary>
    /// Root directory for uploads. Relative paths resolve against the content root. Mirrors the
    /// Node backend's <c>server/uploads</c>.
    /// </summary>
    public string RootPath { get; set; } = "uploads";

    /// <summary>
    /// URL prefix the stored files are served under. The Node backend served the same directory
    /// at BOTH <c>/uploads</c> and <c>/api/files</c> — the second for the Netlify proxy — and
    /// the client rewrites between them, so both must stay mounted.
    /// </summary>
    public string PublicPathPrefix { get; set; } = "/uploads";

    /// <summary>Per-file ceiling in bytes. Node capped profile pictures at 5 MB.</summary>
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Permitted extensions, lowercase and dot-prefixed. An allowlist, never a blocklist: a
    /// blocklist cannot anticipate what the host will decide to execute.
    /// </summary>
    public string[] AllowedExtensions { get; set; } =
    [
        ".jpg", ".jpeg", ".png", ".webp", ".gif",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".csv", ".txt",
        ".mp3", ".wav", ".m4a", ".webm", ".mp4"
    ];
}
