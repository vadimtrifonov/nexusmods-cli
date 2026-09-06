using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NexusMods;

internal sealed record DownloadRequest(string GameDomain, string ModId, string FileId, string ArchiveName, string OutputDirectory, string? ExpectedSize, string[] Mirrors);
internal sealed record DownloadReceipt(string GameDomain, string ModId, string FileId, string ArchiveName, string FinalPath,
    string SizeBytes, string ResumedFromByte, string StartedAt, string CompletedAt);
internal sealed record DownloadResult(DownloadReceipt Receipt, List<PublicError> Errors);
internal sealed record TransferState(
    [property: JsonRequired] int Format,
    [property: JsonRequired] string GameDomain,
    [property: JsonRequired] string ModId,
    [property: JsonRequired] string FileId,
    [property: JsonRequired] string ExpectedSize,
    [property: JsonRequired] string? Etag,
    [property: JsonRequired] string? LastModified);

internal sealed class Download(Cdn cdn, TimeSpan? bodyTimeout = null)
{
    private readonly TimeSpan idleTimeout = bodyTimeout ?? TimeSpan.FromMinutes(5);
    private sealed record Paths(string Final, string Partial, string State, string Lock);
    private sealed record Existing(TransferState State, long Size, long ExpectedSize);

    internal static string ValidateBasename(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
            || Regex.IsMatch(name, "[<>:\"/\\\\|?*\\x00-\\x1f]")
            || Regex.IsMatch(name, @"^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?$", RegexOptions.IgnoreCase))
            throw new CliException("UNSAFE_ARCHIVE_NAME", "Nexus returned an archive name that is not a safe Windows basename.");
        return name;
    }
    private static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static bool Regular(string path) => (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
    private static bool Collision(IOException exception) => (exception.HResult & 0xffff) is 80 or 183;
    private static Paths OutputPaths(DownloadRequest request)
    {
        if (!Path.IsPathFullyQualified(request.OutputDirectory))
            throw new CliException("OUTPUT_DIRECTORY_NOT_ABSOLUTE", "--output-dir must be an absolute path.");
        var directory = Path.GetFullPath(request.OutputDirectory);
        if (!Directory.Exists(directory)) throw new CliException("OUTPUT_DIRECTORY_INVALID", "The output directory does not exist or is not a directory.");
        var final = Path.Combine(directory, ValidateBasename(request.ArchiveName));
        return new(final, final + ".part", final + ".nexus-state.json", final + ".nexus-lock");
    }
    private static (TransferState State, long ExpectedSize) ReadState(string path)
    {
        try
        {
            var state = JsonSerializer.Deserialize<TransferState>(File.ReadAllText(path), App.Json);
            if (state is null || state.Format != 1 || state.GameDomain is null
                || !Regex.IsMatch(state.ModId ?? "", "^[0-9]+$") || !Regex.IsMatch(state.FileId ?? "", "^[0-9]+$")
                || !long.TryParse(state.ExpectedSize, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedSize)
                || state.LastModified is not null && !DateTimeOffset.TryParse(state.LastModified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                throw new JsonException();
            return (state, expectedSize);
        }
        catch (JsonException exception) { throw new CliException("TRANSFER_STATE_INVALID", "The existing transfer-state file has an invalid structure.", innerException: exception); }
    }
    private static Existing? Prepare(Paths paths, DownloadRequest request)
    {
        if (Exists(paths.Final)) throw new CliException("DESTINATION_EXISTS", "The destination file already exists.");
        var partial = Exists(paths.Partial);
        if (partial != Exists(paths.State)) throw new CliException("STALE_TRANSFER", "Only one of the partial file and transfer-state file exists.");
        if (!partial) return null;
        if (!Regular(paths.Partial) || !Regular(paths.State)) throw new CliException("STALE_TRANSFER", "The existing transfer files are not regular files.");
        var (state, expectedSize) = ReadState(paths.State);
        if (state.GameDomain != request.GameDomain || state.ModId != request.ModId || state.FileId != request.FileId)
            throw new CliException("TRANSFER_SOURCE_MISMATCH", "The existing transfer belongs to a different Nexus file.");
        if (request.ExpectedSize is not null && request.ExpectedSize != state.ExpectedSize)
            throw new CliException("TRANSFER_SOURCE_MISMATCH", "Nexus file metadata changed since the transfer began.");
        var size = new FileInfo(paths.Partial).Length;
        if (size > expectedSize)
            throw new CliException("PARTIAL_SIZE_INVALID", "The partial file is larger than its expected size.");
        return new(state, size, expectedSize);
    }
    private static void CleanupError(string path, List<PublicError> errors, string message = "A transfer file could not be removed.") =>
        errors.Add(new("TRANSFER_CLEANUP_FAILED", message, "local-file", "cleanup", Details: new() { ["path"] = path }));
    private static void Remove(string path, List<PublicError> errors)
    {
        try { File.Delete(path); }
        catch { CleanupError(path, errors); }
    }
    private static FileStream OpenPartial(string path, FileMode mode) => new(path, mode, FileAccess.ReadWrite, FileShare.None,
        bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static void WriteState(Stream file, TransferState state) => JsonSerializer.Serialize(file, state, App.Json);
    private static FileStream Create(Paths paths, TransferState state, List<PublicError> errors)
    {
        var partial = OpenPartial(paths.Partial, FileMode.CreateNew);
        var ownedState = false;
        try
        {
            using var file = new FileStream(paths.State, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            ownedState = true;
            WriteState(file, state);
            return partial;
        }
        catch (Exception exception)
        {
            partial.Dispose();
            if (ownedState) Remove(paths.State, errors);
            Remove(paths.Partial, errors);
            throw new CliException("TRANSFER_STATE_CREATE_FAILED", "The transfer-state file could not be created.", innerException: exception);
        }
    }
    private static void ReplaceState(string path, TransferState state, List<PublicError> errors)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        var owned = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned = true;
                WriteState(file, state);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch { if (owned) Remove(temporary, errors); throw; }
    }

    private async Task Copy(HttpResponseMessage response, FileStream output, long expectedBodySize)
    {
        Stream input;
        try { input = await response.Content.ReadAsStreamAsync(); }
        catch (Exception exception) { throw new CliException("DOWNLOAD_INTERRUPTED", "The archive transfer was interrupted.", "nexus-cdn", innerException: exception); }
        // ResponseHeadersRead ends HttpClient's timeout at headers. Bound each body read, not the whole archive.
        using var timeout = new CancellationTokenSource();
        var buffer = new byte[64 * 1024];
        var remaining = expectedBodySize;
        while (true)
        {
            int count;
            try
            {
                timeout.CancelAfter(idleTimeout);
                count = await input.ReadAsync(buffer, timeout.Token);
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            }
            catch (Exception exception) { throw new CliException("DOWNLOAD_INTERRUPTED", "The archive transfer was interrupted.", "nexus-cdn", innerException: exception); }
            if (count == 0) break;
            if (count > remaining) throw new CliException("SIZE_MISMATCH", "The CDN sent more bytes than expected.", "nexus-cdn");
            remaining -= count;
            try { await output.WriteAsync(buffer.AsMemory(0, count)); }
            catch (Exception exception) { throw new CliException("DOWNLOAD_INTERRUPTED", "The archive transfer was interrupted.", "local-file", innerException: exception); }
        }
        if (remaining != 0)
            throw new CliException("SIZE_MISMATCH", "The CDN response body was shorter than expected.", "nexus-cdn",
                new() { ["expected_body_size"] = expectedBodySize.ToString(CultureInfo.InvariantCulture),
                    ["received_body_size"] = (expectedBodySize - remaining).ToString(CultureInfo.InvariantCulture) });
    }

    private async Task<(long Size, long Offset)> Receive(Paths paths, DownloadRequest request, List<PublicError> errors)
    {
        var existing = Prepare(paths, request);
        long? expected = existing?.ExpectedSize;
        if (expected is null && request.ExpectedSize is { } text)
        {
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                throw new CliException("DOWNLOAD_SIZE_UNSUPPORTED", "The download size is outside the supported file-length range.",
                    details: new() { ["maximum_bytes"] = long.MaxValue.ToString(CultureInfo.InvariantCulture) });
            expected = size;
        }
        var prepared = await cdn.Open(request.Mirrors, expected,
            existing is null ? null : new PartialDownload(existing.Size, existing.State.Etag, existing.State.LastModified));
        using var response = prepared.Response;
        FileStream? output = null;
        Exception? failure = null;
        try
        {
            var state = new TransferState(1, request.GameDomain, request.ModId, request.FileId,
                prepared.ExpectedSize.ToString(CultureInfo.InvariantCulture), prepared.Etag, prepared.LastModified);
            if (existing is null) output = Create(paths, state, errors);
            else
            {
                output = OpenPartial(paths.Partial, FileMode.Open);
                if (prepared.Offset == 0)
                {
                    // Publish replacement validators only after old bytes have been removed.
                    output.SetLength(0);
                    ReplaceState(paths.State, state, errors);
                }
                else output.Position = prepared.Offset; // A validated append retains its checkpoint.
            }
            await Copy(response, output, prepared.ExpectedBodySize);
            if (output.Length != prepared.ExpectedSize)
                throw new CliException("SIZE_MISMATCH", "The completed download size differs from the expected size.", "nexus-cdn",
                    new() { ["expected_size"] = prepared.ExpectedSize.ToString(CultureInfo.InvariantCulture), ["received_size"] = output.Length.ToString(CultureInfo.InvariantCulture) });
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            if (output is not null)
                try { await output.DisposeAsync(); }
                catch (Exception exception) { failure ??= exception; CleanupError(paths.Partial, errors, "The partial file could not be closed."); }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return (prepared.ExpectedSize, prepared.Offset);
    }

    internal Task<DownloadResult> Run(DownloadRequest request) => CliException.From("local-file", async () =>
    {
        var started = App.Timestamp(DateTimeOffset.UtcNow);
        var paths = OutputPaths(request);
        FileStream ownership;
        try { ownership = new FileStream(paths.Lock, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException exception) when (Collision(exception) || (exception.HResult & 0xffff) == 32)
        {
            throw new CliException("DOWNLOAD_LOCKED", "The destination is locked. Remove its .nexus-lock file only after confirming no download is running.", innerException: exception);
        }
        var errors = new List<PublicError>();
        DownloadReceipt? receipt = null;
        Exception? failure = null;
        try
        {
            var received = await Receive(paths, request, errors);
            try { File.Move(paths.Partial, paths.Final, overwrite: false); }
            catch (IOException exception) when (Collision(exception)) { throw new CliException("DESTINATION_EXISTS", "The destination appeared before finalization.", innerException: exception); }
            catch (Exception exception) { throw new CliException("FINALIZATION_FAILED", "The completed download could not be finalized atomically.", innerException: exception); }
            // The same-directory, no-replace move is the commit point. Cleanup cannot invalidate the receipt.
            receipt = new(request.GameDomain, request.ModId, request.FileId, request.ArchiveName, paths.Final,
                received.Size.ToString(CultureInfo.InvariantCulture), received.Offset.ToString(CultureInfo.InvariantCulture), started, App.Timestamp(DateTimeOffset.UtcNow));
            Remove(paths.State, errors);
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            try { ownership.Dispose(); }
            catch { CleanupError(paths.Lock, errors, "The download lock could not be closed."); }
            Remove(paths.Lock, errors);
        }
        if (receipt is null)
        {
            var error = CliException.Public(failure!, "local-file");
            if (errors.Count > 0)
            {
                var details = error.Details is null ? new Dictionary<string, object?>() : new(error.Details);
                details["cleanup_errors"] = errors;
                error = error with { Details = details };
            }
            throw new CliException(error, failure);
        }
        return new DownloadResult(receipt, errors);
    });
}
