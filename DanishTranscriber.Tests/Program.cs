using System.Net;
using System.Security.Cryptography;
using DanishTranscriber;
using DocumentFormat.OpenXml.Packaging;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Buffer copies input, stays bounded, and drains in order", () =>
    {
        var buffer = new PcmBuffer(4);
        var first = new byte[] { 1, 2 };
        Check(buffer.TryAppend(first, 2));
        first[0] = 99;
        Check(buffer.TryAppend(new byte[] { 3, 4 }, 2));
        Check(!buffer.TryAppend(new byte[] { 5, 6 }, 2));
        Check(buffer.Drain().SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        Check(buffer.Drain().Length == 0);
        return Task.CompletedTask;
    }),
    ("Silence and steady room noise are rejected", () =>
    {
        Check(!SpeechDetector.ContainsSpeech(new byte[PcmBuffer.BytesPerSecond * 3]));
        Check(!SpeechDetector.ContainsSpeech(ConstantPcm(3, 200)));
        return Task.CompletedTask;
    }),
    ("Speech-like audio is accepted", () =>
    {
        var pcm = new byte[PcmBuffer.BytesPerSecond * 3];
        var tone = TonePcm(0.4, 5000);
        tone.CopyTo(pcm, PcmBuffer.BytesPerSecond);
        Check(SpeechDetector.ContainsSpeech(pcm));
        return Task.CompletedTask;
    }),
    ("Graceful stop flushes and pads final speech", async () =>
    {
        var buffer = new PcmBuffer();
        buffer.TryAppend(new byte[] { 0, 128, 255, 127 }, 4);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var calls = 0;
        await AudioPump.RunAsync(buffer, stop.Token, samples =>
        {
            calls++;
            Check(samples.Length == 16000 && samples[0] == -1f && samples[1] > 0.99f);
            return Task.CompletedTask;
        }, TimeSpan.Zero, _ => true);
        Check(calls == 1);
    }),
    ("Stop during inference drains newly captured audio", async () =>
    {
        var buffer = new PcmBuffer();
        buffer.TryAppend(new byte[] { 1, 0 }, 2);
        using var stop = new CancellationTokenSource();
        var calls = 0;
        await AudioPump.RunAsync(buffer, stop.Token, samples =>
        {
            calls++;
            if (calls == 1)
            {
                buffer.TryAppend(new byte[] { 2, 0 }, 2);
                stop.Cancel();
            }
            return Task.CompletedTask;
        }, TimeSpan.Zero, _ => true);
        Check(calls == 2);
    }),
    ("Valid model cache avoids the network", async () =>
    {
        await WithDirectory(async directory =>
        {
            var bytes = new byte[] { 1, 2, 3 };
            var path = Path.Combine(directory, "model.bin");
            await File.WriteAllBytesAsync(path, bytes);
            using var client = new HttpClient(new StubHandler(() => throw new Exception("Unexpected network")));
            await Cache(client, bytes).EnsureAsync(path);
        });
    }),
    ("Corrupt or partial model never becomes the cache", async () =>
    {
        await WithDirectory(async directory =>
        {
            var path = Path.Combine(directory, "model.bin");
            using var client = new HttpClient(new StubHandler(() =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 9 })
                }));
            await Throws<InvalidDataException>(() => Cache(client, new byte[] { 1 }).EnsureAsync(path));
            Check(!File.Exists(path) && Directory.GetFiles(directory).Length == 0);
        });
    }),
    ("DOCX save preserves Danish text and atomic replacement", async () =>
    {
        await WithDirectory(directory =>
        {
            var path = TranscriptDocument.NewPath(directory);
            TranscriptDocument.Save(path, "Rødgrød med fløde: æ ø å & <tekst>", DateTime.Now);
            using (var document = WordprocessingDocument.Open(path, false))
                Check(document.MainDocumentPart!.Document.InnerText.Contains("Rødgrød med fløde: æ ø å & <tekst>"));
            TranscriptDocument.Save(path, "", DateTime.Now);
            using (var document = WordprocessingDocument.Open(path, false))
                Check(!document.MainDocumentPart!.Document.InnerText.Contains("Rødgrød"));
            Check(Directory.GetFiles(directory).Length == 1);
            return Task.CompletedTask;
        });
    })
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine("PASS " + test.Name);
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine("FAIL " + test.Name + ": " + ex);
    }
}

Console.WriteLine((tests.Length - failed) + "/" + tests.Length + " passed");
return failed == 0 ? 0 : 1;

static byte[] ConstantPcm(double seconds, short value)
{
    var samples = new short[(int)(16000 * seconds)];
    Array.Fill(samples, value);
    var bytes = new byte[samples.Length * 2];
    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
    return bytes;
}

static byte[] TonePcm(double seconds, short amplitude)
{
    var samples = new short[(int)(16000 * seconds)];
    for (var i = 0; i < samples.Length; i++)
        samples[i] = (short)(Math.Sin(2 * Math.PI * 220 * i / 16000) * amplitude);
    var bytes = new byte[samples.Length * 2];
    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
    return bytes;
}

static void Check(bool condition)
{
    if (!condition)
        throw new Exception("Assertion failed");
}

static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

static ModelCache Cache(HttpClient client, byte[] bytes) =>
    new(client, new Uri("https://example.invalid/model"), Convert.ToHexString(SHA256.HashData(bytes)));

static async Task WithDirectory(Func<string, Task> action)
{
    var directory = Path.Combine(Path.GetTempPath(), "DanishTranscriber.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try { await action(directory); }
    finally { Directory.Delete(directory, true); }
}

sealed class StubHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(response());
}
