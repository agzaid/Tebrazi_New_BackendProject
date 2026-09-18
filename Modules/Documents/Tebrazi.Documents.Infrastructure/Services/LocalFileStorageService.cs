using Microsoft.Extensions.Options;
using Tebrazi.Documents.Application.Abstractions;
using Tebrazi.Documents.Application.Configuration;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;

namespace Tebrazi.Documents.Infrastructure.Services;

/// <summary>
/// Stores files on local disk under the configured root, matching the Node backend's
/// <c>server/uploads</c> layout so existing stored URLs keep resolving.
/// </summary>
public sealed class LocalFileStorageService : IFileStorageService
{
    private readonly FileStorageOptions _options;
    private readonly IAppLogger<LocalFileStorageService> _logger;
    private readonly string _rootPath;

    public LocalFileStorageService(
        IOptions<FileStorageOptions> options,
        IAppLogger<LocalFileStorageService> logger)
    {
        _options = options.Value;
        _logger = logger;

        _rootPath = Path.GetFullPath(_options.RootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<string> SaveAsync(
        Stream content,
        string relativePath,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var fullPath = ResolveWithinRoot(relativePath);
        EnsureExtensionAllowed(fullPath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // FileMode.CreateNew, not Create: an existing file is never silently overwritten. The
        // caller decides what a collision means rather than losing the earlier upload.
        await using var target = new FileStream(
            fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        try
        {
            await content.CopyToAsync(target, cancellationToken);
        }
        catch
        {
            // A cancelled or failed copy must not leave a truncated file behind.
            await target.DisposeAsync();
            TryDelete(fullPath);
            throw;
        }

        if (target.Length > _options.MaxFileSizeBytes)
        {
            await target.DisposeAsync();
            TryDelete(fullPath);
            throw new ValidationException(
                $"File exceeds the maximum size of {_options.MaxFileSizeBytes / (1024 * 1024)} MB");
        }

        _logger.Information("File stored", new { relativePath, contentType, target.Length });

        return Normalize(relativePath);
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveWithinRoot(relativePath);

        if (!File.Exists(fullPath))
            throw new NotFoundException("File not found");

        Stream stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default)
        => Task.FromResult(File.Exists(ResolveWithinRoot(relativePath)));

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveWithinRoot(relativePath);
        if (File.Exists(fullPath)) File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public string GetPublicUrl(string relativePath)
        => $"{_options.PublicPathPrefix.TrimEnd('/')}/{Normalize(relativePath).TrimStart('/')}";

    /// <summary>
    /// Resolves a caller-supplied relative path and proves the result is inside the storage root.
    /// This is the path-traversal guard: without it, a stored name of "../../appsettings.json"
    /// reads or writes outside the upload directory.
    /// </summary>
    private string ResolveWithinRoot(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ValidationException("File path is required");

        if (relativePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new ValidationException("File path contains invalid characters");

        var combined = Path.GetFullPath(Path.Combine(_rootPath, Normalize(relativePath).TrimStart('/')));

        // Compare the CANONICAL paths — checking the input string for ".." is not enough, because
        // symlinks and alternate encodings can still escape.
        if (!combined.StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, _rootPath, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warning("Rejected path traversal attempt", new { relativePath });
            throw new ValidationException("Invalid file path");
        }

        return combined;
    }

    private void EnsureExtensionAllowed(string fullPath)
    {
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();

        if (!_options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new ValidationException($"File type '{extension}' is not allowed");
    }

    private static string Normalize(string relativePath) => relativePath.Replace('\\', '/');

    private void TryDelete(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
        catch (IOException ex)
        {
            // Cleanup failure must not mask the original error that triggered it.
            _logger.Warning("Could not remove partial upload", new { fullPath, ex.Message });
        }
    }
}
