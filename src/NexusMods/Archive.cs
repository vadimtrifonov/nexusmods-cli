using System.Globalization;
using System.Security.Cryptography;

namespace NexusMods;

internal sealed record ArchiveHash(string Path, string SizeBytes, string Md5);

internal static class Archive
{
    internal static Task<ArchiveHash> Hash(string inputPath) => CliException.From("local-file", async () =>
    {
        var absolute = Path.GetFullPath(inputPath);
        FileStream file;
        try
        {
            file = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) { throw new CliException("ARCHIVE_UNREADABLE", "The archive could not be opened.", innerException: exception); }
        await using (file)
        {
            try
            {
                if (!file.CanSeek || (File.GetAttributes(file.SafeFileHandle) & FileAttributes.Directory) != 0)
                    throw new CliException("NOT_A_FILE", "The archive path must identify a regular file.");
                var size = RandomAccess.GetLength(file.SafeFileHandle);
                var modified = File.GetLastWriteTimeUtc(file.SafeFileHandle);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                var buffer = new byte[64 * 1024];
                long observed = 0;
                while (true)
                {
                    var count = await file.ReadAsync(buffer);
                    if (count == 0) break;
                    hash.AppendData(buffer, 0, count);
                    observed += count;
                }
                if (observed != size || RandomAccess.GetLength(file.SafeFileHandle) != size || File.GetLastWriteTimeUtc(file.SafeFileHandle) != modified)
                    throw new CliException("ARCHIVE_CHANGED", "The archive size or modification time changed while it was being read.");
                return new ArchiveHash(absolute, observed.ToString(CultureInfo.InvariantCulture), Convert.ToHexStringLower(hash.GetHashAndReset()));
            }
            catch (CliException) { throw; }
            catch (Exception exception) { throw new CliException("ARCHIVE_READ_FAILED", "The archive could not be read completely.", innerException: exception); }
        }
    });
}
