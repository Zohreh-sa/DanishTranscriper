using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace DanishTranscriber;

public sealed class ModelCache(HttpClient client, Uri source, string expectedSha256)
{
    // Digest published by the model owner, pinned to this immutable revision.
    public static readonly Uri SmallModelUrl = new("https://huggingface.co/ggerganov/whisper.cpp/resolve/90a64d80ea254cf67575b41a5971f972c79f7b45/ggml-small.bin");
    public const string SmallModelSha256 = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b";

    private async Task<bool> IsValidAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token))
            .Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    public async Task EnsureAsync(string path, CancellationToken token = default)
    {
        if (await IsValidAsync(path, token)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(file, token);
                await file.FlushAsync(token);
            }
            if (!await IsValidAsync(temporary, token))
                throw new InvalidDataException("The downloaded model failed its integrity check. Please retry.");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
