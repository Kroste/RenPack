using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using RenPack.Services;

namespace RenPack.ViewModels;

/// <summary>UI-Zustand des Ollama-Pull-Fensters. Der Progress-Fluss ist:
/// <c>starte</c> → <c>pulling manifest</c> → mehrere <c>downloading</c>-Events
/// (Bytes-Fortschritt aus dem NDJSON-Stream) → <c>verifying</c> →
/// <c>writing manifest</c> → <c>success</c>. Bei jedem Event wird der Fortschritt
/// hier gemeldet.</summary>
public sealed partial class OllamaPullViewModel : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly OllamaProvider _provider;
    private readonly CancellationTokenSource _cts = new();

    public OllamaPullViewModel(OllamaProvider provider, string modelName)
    {
        _provider = provider;
        _modelName = modelName;
    }

    // Designer-ctor
    public OllamaPullViewModel() : this(
        new OllamaProvider(new System.Net.Http.HttpClient(), "http://localhost:11434", "gemma3:1b"),
        "gemma3:1b") { }

    public event Action<bool>? PullFinished; // bool = success

    [ObservableProperty] private string _modelName;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _phase = "Bereit.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(ProgressValue))]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    private long? _completedBytes;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(ProgressValue))]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    private long? _totalBytes;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private PullState _state = PullState.Idle;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _errorMessage;

    public bool HasProgress => TotalBytes is > 0 && CompletedBytes is not null;
    public double ProgressValue =>
        HasProgress ? (double)CompletedBytes!.Value / TotalBytes!.Value * 100.0 : 0.0;

    public string StatusText => State switch
    {
        PullState.Cancelled => "Abgebrochen.",
        PullState.Failed => ErrorMessage ?? "Fehler.",
        PullState.Succeeded => "Fertig!",
        _ when HasProgress =>
            $"{Phase} — {CompletedBytes!.Value / 1024.0 / 1024.0:F0} / {TotalBytes!.Value / 1024.0 / 1024.0:F0} MB",
        _ => Phase,
    };

    public async Task StartAsync()
    {
        State = PullState.Running;
        Phase = "Starte Pull …";
        try
        {
            await foreach (var evt in _provider.PullAsync(ModelName, _cts.Token))
            {
                if (evt.IsError)
                {
                    ErrorMessage = evt.ErrorMessage ?? "Unbekannter Fehler.";
                    State = PullState.Failed;
                    Log.Warn("Ollama-Pull-Fehler für {model}: {err}", ModelName, evt.ErrorMessage);
                    PullFinished?.Invoke(false);
                    return;
                }
                Phase = evt.Status;
                CompletedBytes = evt.Completed;
                TotalBytes = evt.Total;
                if (evt.Status.Equals("success", StringComparison.OrdinalIgnoreCase))
                {
                    State = PullState.Succeeded;
                    Log.Info("Ollama-Pull erfolgreich: {model}", ModelName);
                    PullFinished?.Invoke(true);
                    return;
                }
            }
            // Stream zu Ende, aber kein explizites "success" — behandeln wir als Erfolg
            // (bei alten Ollama-Versionen fehlt manchmal das Abschluss-Event).
            State = PullState.Succeeded;
            PullFinished?.Invoke(true);
        }
        catch (OperationCanceledException)
        {
            State = PullState.Cancelled;
            Log.Info("Ollama-Pull abgebrochen: {model}", ModelName);
            PullFinished?.Invoke(false);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            State = PullState.Failed;
            Log.Error(ex, "Ollama-Pull fehlgeschlagen: {model}", ModelName);
            PullFinished?.Invoke(false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts.Cancel();
    private bool CanCancel() => State == PullState.Running;

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close() => CloseRequested?.Invoke();
    private bool CanClose() => State != PullState.Running;

    public event Action? CloseRequested;
}

public enum PullState { Idle, Running, Succeeded, Failed, Cancelled }
