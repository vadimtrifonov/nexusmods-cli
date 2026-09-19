namespace NexusMods;

internal sealed record NexusFile(string FileId, string Name, string ArchiveName, string Version, string? Category, string? SizeBytes);
internal sealed record FileMetadata(NexusFile File, Dictionary<string, string>? RateLimits);
internal sealed record DownloadLinks(string[] Urls, Dictionary<string, string>? RateLimits);
internal sealed record AccountStatus(bool Premium, Dictionary<string, string>? RateLimits);

internal sealed class NexusV1(ApiHttp http)
{
    private const string Source = "nexus-v1";
    private const string Root = App.ApiOrigin + "/v1";

    internal Task<AccountStatus> ValidateKey(string key) => CliException.From(Source, async () =>
    {
        var response = await http.Send(Root + "/users/validate.json", Source, key);
        return new AccountStatus(response.Value["is_premium"].Boolean(), response.RateLimits);
    });

    internal Task<FileMetadata> File(string key, string gameDomain, string modId, string fileId) => CliException.From(Source, async () =>
    {
        var response = await http.Send($"{Root}/games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}/files/{fileId}.json", Source, key);
        var file = response.Value.Object();
        var returnedId = file["file_id"].Id();
        if (returnedId != fileId)
            throw new CliException("FILE_ID_MISMATCH", "Nexus returned metadata for a different file.", Source,
                new() { ["requested_file_id"] = fileId, ["returned_file_id"] = returnedId });
        // Nexus sometimes reports zero for nonempty archives; let the CDN supply the size.
        var sizeBytes = file["size_in_bytes"].OptionalId();
        return new FileMetadata(new(returnedId, file["name"].Text(), file["file_name"].Text(), file["version"].Text(),
            file["category_name"].OptionalText(), sizeBytes == "0" ? null : sizeBytes), response.RateLimits);
    });

    internal Task<DownloadLinks> Links(string key, string gameDomain, string modId, string fileId) => CliException.From(Source, async () =>
    {
        ApiResponse response;
        try { response = await http.Send($"{Root}/games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}/files/{fileId}/download_link.json", Source, key); }
        catch (CliException exception) when (exception.Error.HttpStatus == 403)
        {
            throw new CliException(new PublicError("DOWNLOAD_NOT_AUTHORIZED",
                "Nexus did not authorize a direct download. A Premium account is required.", Source, HttpStatus: 403), exception);
        }
        var urls = response.Value.Array().Select(value => value["URI"].Text()).ToArray();
        if (urls.Length == 0) throw new CliException("NO_DOWNLOAD_LINKS", "Nexus returned no download mirrors.", Source);
        return new DownloadLinks(urls, response.RateLimits);
    });
}
