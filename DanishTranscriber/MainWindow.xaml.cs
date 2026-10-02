using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using NAudio.Wave;
using Whisper.net;

namespace DanishTranscriber;

public partial class MainWindow : Window
{
    const int SampleRate = 16000;

    WaveIn? microphone;
    PcmBuffer? audio;
    CancellationTokenSource? sessionStop;
    Task? transcriptionWorker;
    string? docxPath;
    string? lastAcceptedText;
    DateTime sessionStarted;
    int overflowReported;
    bool closeAfterStop;
    bool closeInProgress;

    readonly SemaphoreSlim saveLock = new(1, 1);
    readonly SemaphoreSlim stopLock = new(1, 1);
    readonly string appDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DanishTranscriber");

    string ModelPath => Path.Combine(appDirectory, "ggml-small.bin");

    public MainWindow() => InitializeComponent();

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        try
        {
            TranscriptBox.Clear();
            lastAcceptedText = null;
            overflowReported = 0;
            sessionStarted = DateTime.Now;

            Directory.CreateDirectory(appDirectory);
            await EnsureModelAsync();

            var transcriptDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Danish Transcripts");
            Directory.CreateDirectory(transcriptDirectory);
            docxPath = TranscriptDocument.NewPath(transcriptDirectory);
            await SaveDocxAsync();

            audio = new PcmBuffer();
            sessionStop = new CancellationTokenSource();
            microphone = new WaveIn
            {
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 100
            };
            microphone.DataAvailable += Microphone_DataAvailable;

            transcriptionWorker = RunTranscriptionAsync(audio, sessionStop.Token);
            _ = ObserveWorkerAsync(transcriptionWorker, sessionStop);
            microphone.StartRecording();

            StopButton.IsEnabled = true;
            StatusText.Text = "Listening • local/offline transcription";
        }
        catch (Exception ex)
        {
            await ReleaseSessionResourcesAsync();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StatusText.Text = "Error";
            MessageBox.Show(ex.Message, "Could not start");
        }
    }

    void Microphone_DataAvailable(object? sender, WaveInEventArgs e)
    {
        var currentAudio = audio;
        if (currentAudio is null || currentAudio.TryAppend(e.Buffer, e.BytesRecorded))
            return;

        if (Interlocked.Exchange(ref overflowReported, 1) == 0)
        {
            _ = RunOnUiAsync(async () =>
            {
                MessageBox.Show(
                    "Transcription could not keep up with the microphone. Recording was stopped to avoid losing more audio.",
                    "Audio buffer full");
                await StopSessionAsync("Stopped: audio buffer full");
            });
        }
    }

    async Task EnsureModelAsync()
    {
        StatusText.Text = "Checking the local Whisper model…";
        using var client = new HttpClient { Timeout = TimeSpan.FromHours(1) };
        var cache = new ModelCache(client, ModelCache.SmallModelUrl, ModelCache.SmallModelSha256);
        if (!File.Exists(ModelPath))
            StatusText.Text = "First run: downloading Whisper small model (~488 MB)…";
        await cache.EnsureAsync(ModelPath);
    }

    async Task RunTranscriptionAsync(PcmBuffer currentAudio, CancellationToken stop)
    {
        using var factory = WhisperFactory.FromPath(ModelPath);
        using var processor = factory.CreateBuilder().WithLanguage("da").Build();

        await AudioPump.RunAsync(currentAudio, stop, async samples =>
        {
            await Dispatcher.InvokeAsync(() => StatusText.Text = "Transcribing locally…");
            var wav = BuildWav(samples);
            await using var wavStream = new MemoryStream(wav);
            var parts = new List<string>();

            // Stop requests are graceful; do not cancel an inference that already
            // owns audio, otherwise the last spoken sentence can be lost.
            await foreach (var segment in processor.ProcessAsync(wavStream, CancellationToken.None))
            {
                var text = segment.Text.Trim();
                if (!string.IsNullOrWhiteSpace(text) && !IsSilenceMarker(text))
                    parts.Add(text);
            }

            var transcription = string.Join(" ", parts).Trim();
            if (!string.IsNullOrWhiteSpace(transcription))
                await AppendTranscriptionAsync(transcription);

            await Dispatcher.InvokeAsync(() => StatusText.Text = "Listening");
        });
    }

    async Task AppendTranscriptionAsync(string transcription)
    {
        var appended = false;
        await Dispatcher.InvokeAsync(() =>
        {
            var normalized = NormalizeForComparison(transcription);
            if (normalized.Length == 0 || normalized == lastAcceptedText)
                return;

            TranscriptBox.AppendText((TranscriptBox.Text.Length > 0 ? " " : "") + transcription);
            TranscriptBox.ScrollToEnd();
            lastAcceptedText = normalized;
            appended = true;
        });

        if (appended)
            await SaveDocxAsync();
    }

    async Task ObserveWorkerAsync(Task worker, CancellationTokenSource owner)
    {
        try
        {
            await worker;
        }
        catch (Exception ex) when (!owner.IsCancellationRequested)
        {
            await RunOnUiAsync(async () =>
            {
                MessageBox.Show(ex.Message, "Transcription error");
                await StopSessionAsync("Stopped after transcription error");
            });
        }
    }

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await StopSessionAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not stop cleanly");
            await ReleaseSessionResourcesAsync();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    async Task RunOnUiAsync(Func<Task> action)
    {
        if (Dispatcher.CheckAccess())
            await action();
        else
            await Dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    async Task StopSessionAsync(string? status = null)
    {
        await stopLock.WaitAsync();
        try
        {
            StopButton.IsEnabled = false;

            var capture = microphone;
            if (capture is not null)
            {
                await StopCaptureAsync(capture);
                capture.DataAvailable -= Microphone_DataAvailable;
                capture.Dispose();
                if (ReferenceEquals(microphone, capture))
                    microphone = null;
            }

            sessionStop?.Cancel();
            if (transcriptionWorker is not null)
            {
                try
                {
                    await transcriptionWorker;
                }
                catch (Exception ex) when (sessionStop?.IsCancellationRequested == true)
                {
                    MessageBox.Show(ex.Message, "Could not finish transcription");
                }
            }

            await SaveDocxAsync();
            audio?.Clear();
            audio = null;
            sessionStop?.Dispose();
            sessionStop = null;
            transcriptionWorker = null;

            StatusText.Text = status ?? $"Saved: {docxPath}";
            StartButton.IsEnabled = !closeInProgress;
        }
        finally
        {
            stopLock.Release();
        }
    }

    static async Task StopCaptureAsync(WaveIn capture)
    {
        var stopped = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, StoppedEventArgs args) => stopped.TrySetResult(args);
        capture.RecordingStopped += Handler;
        try
        {
            capture.StopRecording();
            var result = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (result.Exception is not null)
                throw new InvalidOperationException("The microphone stopped with an error.", result.Exception);
        }
        finally
        {
            capture.RecordingStopped -= Handler;
        }
    }

    async Task ReleaseSessionResourcesAsync()
    {
        sessionStop?.Cancel();
        if (microphone is not null)
        {
            microphone.DataAvailable -= Microphone_DataAvailable;
            microphone.Dispose();
            microphone = null;
        }
        if (transcriptionWorker is not null)
        {
            try { await transcriptionWorker; }
            catch { }
        }
        audio?.Clear();
        audio = null;
        sessionStop?.Dispose();
        sessionStop = null;
        transcriptionWorker = null;
    }

    async Task SaveDocxAsync()
    {
        var path = docxPath;
        if (path is null)
            return;

        await saveLock.WaitAsync();
        try
        {
            var text = await Dispatcher.InvokeAsync(() => TranscriptBox.Text ?? "");
            await Task.Run(() => TranscriptDocument.Save(path, text, sessionStarted));
        }
        finally
        {
            saveLock.Release();
        }
    }

    async void Clear_Click(object sender, RoutedEventArgs e)
    {
        TranscriptBox.Clear();
        lastAcceptedText = null;
        try
        {
            await SaveDocxAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not update the transcript file");
        }
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(TranscriptBox.Text))
                Clipboard.SetText(TranscriptBox.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not copy the transcript");
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (closeAfterStop)
        {
            base.OnClosing(e);
            return;
        }

        if (microphone is null && transcriptionWorker is null)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (closeInProgress)
            return;

        closeInProgress = true;
        try
        {
            await StopSessionAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not finish saving");
        }
        finally
        {
            closeAfterStop = true;
            Close();
        }
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
        return normalized is
            "silence" or "blank audio" or "music" or "musik" or
            "tak fordi du så med" or "tak for at du så med" or
            "undertekster af amara org";
    }

    static byte[] BuildWav(float[] samples)
    {
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
        {
            var dataLength = samples.Length * 2;
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            foreach (var sample in samples)
            {
                var clamped = Math.Clamp(sample, -1f, 1f);
                writer.Write((short)Math.Round(clamped * (clamped < 0 ? 32768f : 32767f)));
            }
        }
        return memory.ToArray();
    }
}
