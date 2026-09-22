using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using NAudio.Wave;
using Whisper.net;

namespace DanishTranscriber;

public partial class MainWindow : Window
{
    const int SampleRate = 16000;
    const int ProcessingIntervalMs = 3000;
    const int MinimumAudioBytes = SampleRate * 2; // One second of 16-bit mono audio.

    WaveInEvent? mic;
    MemoryStream? audio;
    CancellationTokenSource? cts;
    string? docxPath;
    string? lastAcceptedText;
    readonly SemaphoreSlim saveLock = new(1, 1);
    readonly string appDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DanishTranscriber");

    string ModelPath => Path.Combine(appDir, "ggml-small.bin");

    public MainWindow() => InitializeComponent();

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartButton.IsEnabled = false;
            TranscriptBox.Clear();
            lastAcceptedText = null;

            Directory.CreateDirectory(appDir);
            await EnsureModelAsync();

            var transcriptDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Danish Transcripts");
            Directory.CreateDirectory(transcriptDirectory);
            docxPath = Path.Combine(transcriptDirectory, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.docx");
            await SaveDocxAsync();

            audio = new MemoryStream();
            mic = new WaveInEvent
            {
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 100
            };
            mic.DataAvailable += Mic_DataAvailable;
            mic.StartRecording();

            cts = new CancellationTokenSource();
            StopButton.IsEnabled = true;
            StatusText.Text = "Listening • local/offline transcription";
            _ = RunTranscriptionLoopAsync(cts.Token);
        }
        catch (Exception ex)
        {
            StartButton.IsEnabled = true;
            StatusText.Text = "Error";
            MessageBox.Show(ex.ToString(), "Could not start");
        }
    }

    void Mic_DataAvailable(object? sender, WaveInEventArgs e)
    {
        var currentAudio = audio;
        if (currentAudio is null)
            return;

        lock (currentAudio)
            currentAudio.Write(e.Buffer, 0, e.BytesRecorded);
    }

    async Task EnsureModelAsync()
    {
        if (File.Exists(ModelPath))
            return;

        StatusText.Text = "First run: downloading Whisper small model (~466 MB)…";
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromHours(1) };
        using var response = await httpClient.GetAsync(
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var modelStream = await response.Content.ReadAsStreamAsync();
        await using var modelFile = File.Create(ModelPath);
        await modelStream.CopyToAsync(modelFile);
    }

    async Task RunTranscriptionLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var factory = WhisperFactory.FromPath(ModelPath);
            using var processor = factory.CreateBuilder().WithLanguage("da").Build();

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(ProcessingIntervalMs, cancellationToken);
                var pcm = TakePendingAudio();

                // Do not ask Whisper to transcribe quiet chunks. Whisper can otherwise
                // hallucinate or repeat the last sentence when it receives silence.
                if (pcm.Length < MinimumAudioBytes || !ContainsSpeech(pcm))
                {
                    StatusText.Text = "Listening";
                    continue;
                }

                StatusText.Text = "Transcribing locally…";
                var wav = BuildWav(pcm);
                await using var wavStream = new MemoryStream(wav);
                var parts = new List<string>();

                await foreach (var segment in processor.ProcessAsync(wavStream, cancellationToken))
                {
                    var text = segment.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(text) && !IsSilenceMarker(text))
                        parts.Add(text);
                }

                var transcription = string.Join(" ", parts).Trim();
                if (!string.IsNullOrWhiteSpace(transcription) && !IsImmediateDuplicate(transcription))
                {
                    TranscriptBox.AppendText((TranscriptBox.Text.Length > 0 ? " " : "") + transcription);
                    TranscriptBox.ScrollToEnd();
                    lastAcceptedText = NormalizeForComparison(transcription);
                    await SaveDocxAsync();
                }

                StatusText.Text = "Listening";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal when Stop is pressed or the window closes.
        }
        catch (Exception ex)
        {
            StatusText.Text = "Transcription error";
            MessageBox.Show(ex.ToString(), "Transcription error");
        }
    }

    byte[] TakePendingAudio()
    {
        var currentAudio = audio;
        if (currentAudio is null)
            return Array.Empty<byte>();

        lock (currentAudio)
        {
            var pcm = currentAudio.ToArray();
            currentAudio.SetLength(0);
            currentAudio.Position = 0;
            return pcm;
        }
    }

    static bool ContainsSpeech(byte[] pcm)
    {
        // Analyse 20 ms frames. A chunk counts as speech only when several frames
        // have both useful average energy and a clear peak, which rejects room noise
        // and isolated clicks while still allowing normal classroom speech.
        const int bytesPerSample = 2;
        const int frameSamples = SampleRate / 50;
        const double rmsThreshold = 0.008;
        const double peakThreshold = 0.025;
        const int requiredSpeechFrames = 4;

        var speechFrames = 0;
        for (var frameStart = 0; frameStart + frameSamples * bytesPerSample <= pcm.Length; frameStart += frameSamples * bytesPerSample)
        {
            double sumSquares = 0;
            var peak = 0;

            for (var i = 0; i < frameSamples; i++)
            {
                var offset = frameStart + i * bytesPerSample;
                var sample = (short)(pcm[offset] | (pcm[offset + 1] << 8));
                var absoluteSample = Math.Abs((int)sample);
                peak = Math.Max(peak, absoluteSample);
                sumSquares += (double)sample * sample;
            }

            var rms = Math.Sqrt(sumSquares / frameSamples) / short.MaxValue;
            var normalizedPeak = peak / (double)short.MaxValue;
            if (rms >= rmsThreshold && normalizedPeak >= peakThreshold)
            {
                speechFrames++;
                if (speechFrames >= requiredSpeechFrames)
                    return true;
            }
        }

        return false;
    }

    bool IsImmediateDuplicate(string text)
    {
        var normalized = NormalizeForComparison(text);
        return normalized.Length > 0 && normalized == lastAcceptedText;
    }

    static string NormalizeForComparison(string text)
    {
        var lower = text.ToLowerInvariant();
        var withoutPunctuation = Regex.Replace(lower, @"[^\p{L}\p{N}\s]", " ");
        return Regex.Replace(withoutPunctuation, @"\s+", " ").Trim();
    }

    static bool IsSilenceMarker(string text)
    {
        var normalized = NormalizeForComparison(text);
        return normalized is "silence" or "blank audio" or "music" or "musik";
    }

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        cts?.Cancel();
        if (mic is not null)
        {
            mic.DataAvailable -= Mic_DataAvailable;
            mic.StopRecording();
            mic.Dispose();
            mic = null;
        }

        await SaveDocxAsync();
        StatusText.Text = $"Saved: {docxPath}";
        StopButton.IsEnabled = false;
        StartButton.IsEnabled = true;
    }

    async Task SaveDocxAsync()
    {
        if (docxPath is null)
            return;

        await saveLock.WaitAsync();
        try
        {
            var temporaryPath = docxPath + ".tmp";
            using (var document = WordprocessingDocument.Create(
                       temporaryPath,
                       DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var mainPart = document.AddMainDocumentPart();
                mainPart.Document = new Document(
                    new Body(
                        new Paragraph(new Run(new Text($"Danish transcript — {DateTime.Now:yyyy-MM-dd HH:mm}"))),
                        new Paragraph(
                            new Run(
                                new Text(TranscriptBox.Text ?? "")
                                {
                                    Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve
                                }))));
                mainPart.Document.Save();
            }

            File.Move(temporaryPath, docxPath, true);
        }
        finally
        {
            saveLock.Release();
        }
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        TranscriptBox.Clear();
        lastAcceptedText = null;
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TranscriptBox.Text))
            Clipboard.SetText(TranscriptBox.Text);
    }

    protected override void OnClosed(EventArgs e)
    {
        cts?.Cancel();
        if (mic is not null)
        {
            mic.DataAvailable -= Mic_DataAvailable;
            mic.StopRecording();
            mic.Dispose();
        }
        base.OnClosed(e);
    }

    static byte[] BuildWav(byte[] pcm)
    {
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + pcm.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }
        return memory.ToArray();
    }
}
