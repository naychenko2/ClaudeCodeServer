using ClaudeHomeServer.Services.Higgsfield;

namespace ClaudeHomeServer.Services.ImageEditor;

// Картиночная обёртка над транспортом Higgsfield из Core: без названного типа — image/png,
// как у ImageDownload
internal static class HiggsfieldImageDownload
{
    public static async Task<EditedImage?> DownloadAsync(this HiggsfieldMcpClient client, string url, CancellationToken ct) =>
        await client.DownloadBytesAsync(url, ct) is { } file
            ? new EditedImage(file.Bytes, file.ContentType ?? "image/png")
            : null;
}
