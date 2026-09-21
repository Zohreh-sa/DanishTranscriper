using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using NAudio.Wave;
using Whisper.net;

namespace DanishTranscriber;

public partial class MainWindow : Window
{
    private readonly PcmBuffer audio = new();
    private readonly string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DanishTranscriber");
    private WaveInEvent? mic;
    private CancellationTokenSource? startupCancellation;
    private CancellationTokenSource? stopDelay;
    private Task? startupTask;
    private Task? worker;
    private Task? stoppingTask;
    private TaskCompletionSource<bool>? recordingStopped;
    private string? docxPath;
    private DateTime started;
    private string? sessionError;
    private bool closing;
    private bool allowClose;
    private bool recordingStarted;
    private volatile bool acceptAudio;
    private string ModelPath => Path.Combine(appDir, "ggml-small.bin");

    public MainWindow() => InitializeComponent();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (startupTask is { IsCompleted: false } || mic != null || closing) return;
        startupTask = StartAsync();
        await startupTask;
    }

    private async Task StartAsync()
    {
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        stoppingTask = null;
        worker = null;
        sessionError = null;
        docxPath = null;
        audio.Clear();
        startupCancellation = new CancellationTokenSource();
        try
        {
            StatusText.Text = "Checking model / downloading on first run (~466 MiB)…";
            using var client = new HttpClient { Timeout = TimeSpan.FromHours(1) };
            await new ModelCache(client, ModelCache.SmallModelUrl, ModelCache.SmallModelSha256)
                .EnsureAsync(ModelPath, startupCancellation.Token);
            startupCancellation.Token.ThrowIfCancellationRequested();

            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Danish Transcripts");
            Directory.CreateDirectory(directory);
            docxPath = TranscriptDocument.NewPath(directory);
            started = DateTime.Now;
            TranscriptDocument.Save(docxPath, "", started);
            TranscriptBox.Clear();
            stopDelay = new CancellationTokenSource();
            recordingStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
            mic.DataAvailable += OnDataAvailable;
            mic.RecordingStopped += OnRecordingStopped;
            acceptAudio = true;
            mic.StartRecording();
            recordingStarted = true;
            var stop = stopDelay.Token;
            worker = Task.Run(() => TranscribeAsync(stop));
            StopButton.IsEnabled = true;
            StatusText.Text = "Listening • local/offline transcription";
        }
        catch (OperationCanceledException) when (startupCancellation.IsCancellationRequested)
        {
            StatusText.Text = "Start cancelled";
        }
        catch (Exception ex)
        {
            sessionError = ex.Message;
            await StopSessionAsync();
        }
        finally
        {
            startupCancellation.Dispose();
            startupCancellation = null;
            if (mic is null && !closing) StartButton.IsEnabled = true;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!acceptAudio) return;
        if (!audio.TryAppend(e.Buffer, e.BytesRecorded))
        {
            acceptAudio = false;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (!ReferenceEquals(sender, mic)) return;
                sessionError ??= "Transcription could not keep up. Recording stopped because the audio buffer is full; some audio was not captured.";
                await StopSessionAsync();
            }));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (!ReferenceEquals(sender, mic)) return;
            acceptAudio = false;
            if (e.Exception != null) sessionError ??= e.Exception.Message;
            recordingStopped?.TrySetResult(true);
            if (stoppingTask is null) await StopSessionAsync();
        }));
    }

    private async Task TranscribeAsync(CancellationToken stop)
    {
        try
        {
            using var factory = WhisperFactory.FromPath(ModelPath);
            using var processor = factory.CreateBuilder().WithLanguage("da").Build();
            await AudioPump.RunAsync(audio, stop, async samples =>
            {
                var parts = new List<string>();
                await foreach (var segment in processor.ProcessAsync(samples))
                    if (!string.IsNullOrWhiteSpace(segment.Text)) parts.Add(segment.Text.Trim());
                if (parts.Count > 0)
                    await Dispatcher.InvokeAsync(() =>
                    {
                        TranscriptBox.AppendText((TranscriptBox.Text.Length > 0 ? " " : "") + string.Join(" ", parts));
                        TranscriptBox.ScrollToEnd();
                        SaveTranscript();
                    });
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                sessionError ??= "Transcription failed: " + ex.Message;
                Dispatcher.BeginInvoke(new Action(async () => await StopSessionAsync()));
            });
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopSessionAsync();
    private Task StopSessionAsync() => stoppingTask ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        // Store stoppingTask before any callback or error dialog can re-enter Stop.
        await Task.Yield();
        StopButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        StatusText.Text = "Finishing transcription and saving…";
        try
        {
            if (mic != null && recordingStarted)
            {
                try
                {
                    mic.StopRecording();
                    // NAudio delivers the last capture buffers before RecordingStopped.
                    await recordingStopped!.Task;
                }
                catch (Exception ex)
                {
                    sessionError ??= "Could not stop microphone: " + ex.Message;
                    DisposeMicrophone();
                }
            }
            stopDelay?.Cancel();
            if (worker != null) await worker;
            SaveTranscript();
        }
        catch (Exception ex) { sessionError ??= "Could not finish saving: " + ex.Message; }
        finally
        {
            DisposeMicrophone();
            stopDelay?.Dispose();
            stopDelay = null;
            audio.Clear();
            StartButton.IsEnabled = !closing;
            StatusText.Text = sessionError is null ? $"Saved: {docxPath}" : "Error: " + sessionError;
            if (sessionError != null)
                MessageBox.Show(this, sessionError + "\nThe visible text can still be copied.", "Transcription error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DisposeMicrophone()
    {
        acceptAudio = false;
        var capture = mic;
        mic = null;
        recordingStarted = false;
        if (capture is null) return;
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        try { capture.Dispose(); }
        catch (Exception ex) { sessionError ??= "Could not release microphone: " + ex.Message; }
    }

    private void SaveTranscript()
    {
        if (docxPath != null) TranscriptDocument.Save(docxPath, TranscriptBox.Text, started);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        TranscriptBox.Clear();
        try { SaveTranscript(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save cleared transcript"); }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { if (TranscriptBox.Text.Length > 0) Clipboard.SetText(TranscriptBox.Text); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not copy transcript"); }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (allowClose) { base.OnClosing(e); return; }
        e.Cancel = true;
        base.OnClosing(e);
        if (closing) return;
        closing = true;
        startupCancellation?.Cancel();
        if (startupTask != null) await startupTask;
        if (mic != null || stoppingTask != null) await StopSessionAsync();
        if (sessionError != null)
        {
            closing = false;
            StartButton.IsEnabled = true;
            if (MessageBox.Show(this, "An error occurred. Close anyway? Copy any needed text first.", "Close transcript", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }
        allowClose = true;
        Close();
    }
}
