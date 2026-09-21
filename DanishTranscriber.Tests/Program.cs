using System.Net;
using System.Security.Cryptography;
using DanishTranscriber;
using DocumentFormat.OpenXml.Packaging;

// Dependency-free regression runner; exits nonzero on any failed assertion.
var tests = new (string Name, Func<Task> Run)[]
{
    ("Bounded buffer rejects overflow and drains in order", () =>
    {
        var buffer = new PcmBuffer(4);
        var first = new byte[] { 1, 2 };
        Check(buffer.TryAppend(first, 2));
        first[0] = 99; // capture devices reuse their buffers
        Check(buffer.TryAppend(new byte[] { 3, 4 }, 2));
        Check(!buffer.TryAppend(new byte[] { 5, 6 }, 2));
        Check(buffer.Drain().SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        Check(buffer.Drain().Length == 0);
        Check(buffer.TryAppend(new byte[] { 5, 6 }, 2));
        buffer.Clear();
        Check(buffer.Drain().Length == 0);
        return Task.CompletedTask;
    }),
    ("Stop before first interval flushes and pads short speech", async () =>
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
            Check(samples.Skip(2).All(value => value == 0));
            return Task.CompletedTask;
        });
        Check(calls == 1 && buffer.Drain().Length == 0);
    }),
    ("Stop during inference drains audio captured during that inference", async () =>
    {
        var buffer = new PcmBuffer();
        buffer.TryAppend(new byte[] { 1, 0 }, 2);
        using var stop = new CancellationTokenSource();
        var calls = 0;
        await AudioPump.RunAsync(buffer, stop.Token, samples =>
        {
            calls++;
            Check(samples[0] == calls / 32768f);
            if (calls == 1)
            {
                buffer.TryAppend(new byte[] { 2, 0 }, 2);
                stop.Cancel();
            }
            return Task.CompletedTask;
        }, TimeSpan.Zero);
        Check(calls == 2);
    }),
    ("Backlog is split into bounded inference calls", async () =>
    {
        var buffer = new PcmBuffer();
        var pcm = new byte[PcmBuffer.BytesPerSecond * 25];
        buffer.TryAppend(pcm, pcm.Length);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var sizes = new List<int>();
        await AudioPump.RunAsync(buffer, stop.Token, samples =>
        {
            sizes.Add(samples.Length);
            return Task.CompletedTask;
        });
        Check(sizes.SequenceEqual(new[] { 16000 * 12, 16000 * 12, 16000 }));
    }),
    ("Inference errors propagate and sample arrays are cleared", async () =>
    {
        var buffer = new PcmBuffer();
        buffer.TryAppend(new byte[] { 1, 0 }, 2);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        float[]? retained = null;
        await Throws<InvalidOperationException>(() => AudioPump.RunAsync(buffer, stop.Token, samples =>
        {
            retained = samples;
            throw new InvalidOperationException("inference failed");
        }));
        Check(retained != null && retained.All(value => value == 0));
    }),
    ("Valid cached model requires no network", async () =>
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
    ("Corrupt cache is replaced only by a verified download", async () =>
    {
        await WithDirectory(async directory =>
        {
            var bytes = new byte[] { 1, 2, 3 };
            var path = Path.Combine(directory, "model.bin");
            await File.WriteAllBytesAsync(path, new byte[] { 0 });
            using var client = new HttpClient(new StubHandler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            await Cache(client, bytes).EnsureAsync(path);
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes));
            Check(Directory.GetFiles(directory).Length == 1);
        });
    }),
    ("Bad model digest cannot become a usable cached model", async () =>
    {
        await WithDirectory(async directory =>
        {
            var path = Path.Combine(directory, "model.bin");
            using var client = new HttpClient(new StubHandler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 9 }) }));
            await Throws<InvalidDataException>(() => Cache(client, new byte[] { 1 }).EnsureAsync(path));
            Check(!File.Exists(path) && Directory.GetFiles(directory).Length == 0);
        });
    }),
    ("Cancelled download leaves no partial cache", async () =>
    {
        await WithDirectory(async directory =>
        {
            using var cancellation = new CancellationTokenSource();
            using var client = new HttpClient(new StubHandler(() =>
            {
                cancellation.Cancel();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) };
            }));
            await Throws<OperationCanceledException>(() => Cache(client, new byte[] { 1 })
                .EnsureAsync(Path.Combine(directory, "model.bin"), cancellation.Token));
            Check(Directory.GetFiles(directory).Length == 0);
        });
    }),
    ("DOCX round trip preserves Danish text and clearing", async () =>
    {
        await WithDirectory(directory =>
        {
            var path = TranscriptDocument.NewPath(directory);
            Check(path != TranscriptDocument.NewPath(directory));
            TranscriptDocument.Save(path, "Rødgrød med fløde: æ ø å & <tekst>", DateTime.Now);
            using (var document = WordprocessingDocument.Open(path, false))
                Check(document.MainDocumentPart!.Document.InnerText.Contains("Rødgrød med fløde: æ ø å & <tekst>"));
            TranscriptDocument.Save(path, "", DateTime.Now);
            using (var document = WordprocessingDocument.Open(path, false))
                Check(!document.MainDocumentPart!.Document.InnerText.Contains("Rødgrød"));
            Check(Directory.GetFiles(directory).Length == 1);
            return Task.CompletedTask;
        });
    }),
    ("Failed save preserves the previous document and cleans temporary text", async () =>
    {
        await WithDirectory(directory =>
        {
            var path = TranscriptDocument.NewPath(directory);
            TranscriptDocument.Save(path, "Original", DateTime.Now);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var refused = false;
                try { TranscriptDocument.Save(path, "New", DateTime.Now); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { refused = true; }
                Check(refused);
            }
            using var doc = WordprocessingDocument.Open(path, false);
            Check(doc.MainDocumentPart!.Document.InnerText.Contains("Original"));
            Check(Directory.GetFiles(directory).Length == 1);
            return Task.CompletedTask;
        });
    })
};

var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + ex); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static ModelCache Cache(HttpClient client, byte[] bytes) => new(client, new Uri("https://example.invalid/model"), Convert.ToHexString(SHA256.HashData(bytes)));
static async Task WithDirectory(Func<string, Task> action)
{
    var directory = Path.Combine(Path.GetTempPath(), "DanishTranscriber.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try { await action(directory); }
    finally { Directory.Delete(directory, true); }
}

sealed class StubHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
}
