namespace Tebrazi.Documents.Application.Abstractions;

/// <summary>
/// The published port for file storage. Every module that saves a file — health vault
/// documents, profile pictures, prescription PDFs, intake attachments — goes through this and
/// never touches <see cref="System.IO"/> directly.
///
/// This is the boundary the KACCC Documents module got right and the others did not: the
/// implementation can move from local disk to blob storage without any consumer changing.
/// </summary>
public interface IFileStorageService
{
    /// <summary>
    /// Persists a stream at a storage-relative path and returns the path actually written,
    /// which may differ from the requested one if a collision was resolved.
    /// </summary>
    Task<string> SaveAsync(
        Stream content,
        string relativePath,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a stored file for reading. Throws <see cref="FileNotFoundException"/> if absent.</summary>
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Deletes if present. Deleting a missing file is not an error.</summary>
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// The public URL for a stored file — the value written into a database column and returned
    /// to the client, e.g. <c>/uploads/profile-pictures/abc.jpg</c>.
    /// </summary>
    string GetPublicUrl(string relativePath);
}
