using System.Collections.ObjectModel;
using System.Windows.Input;
using HealthTracker.Services;
using HealthTracker.Services.Ai;

namespace HealthTracker.ViewModels;

/// <summary>One chat bubble. Errors are their own kind of row so the page can offer a fix (retry, add key).</summary>
public class ChatMessage
{
    public string Text { get; set; } = string.Empty;
    public bool IsUser { get; set; }
    public bool IsError { get; set; }
    public GeminiErrorKind ErrorKind { get; set; } = GeminiErrorKind.None;

    /// <summary>Network failures get a Retry button.</summary>
    // Network failures and overload/rate limits are transient, so both get a Retry button.
    public bool CanRetry => IsError && ErrorKind is GeminiErrorKind.Network or GeminiErrorKind.RateLimited;

    /// <summary>Missing key gets an "Add Gemini key" shortcut to Settings.</summary>
    public bool IsNoApiKey => IsError && ErrorKind == GeminiErrorKind.NoApiKey;

    /// <summary>Everything else shows the plain message.</summary>
    public bool IsPlainError => IsError && !CanRetry && !IsNoApiKey;
}

public class AskAiViewModel : BaseViewModel
{
    private readonly AiChatService _chatService;
    private string _inputText = string.Empty;
    private string? _lastQuestion;
    private CancellationTokenSource? _cts;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public IReadOnlyList<string> SuggestedQuestions { get; } = new[]
    {
        "How is my adherence this week?",
        "Summarise my latest report",
        "When is my next follow-up?",
        "What's my blood pressure trend?",
    };

    public string InputText
    {
        get => _inputText;
        set { _inputText = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSend)); ((Command)SendCommand).ChangeCanExecute(); }
    }

    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(InputText);

    public bool HasMessages => Messages.Count > 0;

    public ICommand SendCommand { get; }
    public ICommand SuggestCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    /// <summary>The page navigates to SettingsPage on this.</summary>
    public event EventHandler? OpenSettingsRequested;

    /// <summary>Raised after a message is appended so the page can scroll to it.</summary>
    public event EventHandler<ChatMessage>? MessageAppended;

    // Conversation history for multi-turn context (role, text). Errors are not part of it.
    private readonly List<(string Role, string Text)> _history = new();

    public AskAiViewModel(AiChatService chatService)
    {
        _chatService = chatService;
        SendCommand = new Command(async () => await Send(InputText), () => CanSend);
        SuggestCommand = new Command<string>(async q => await Send(q));
        RetryCommand = new Command(async () => await Retry());
        ClearCommand = new Command(Clear);
        OpenSettingsCommand = new Command(() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMessages));
    }

    private async Task Send(string? text)
    {
        var question = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(question) || IsBusy) return;

        // Drop a trailing error row so a retry reads as one conversation.
        if (Messages.Count > 0 && Messages[^1].IsError) Messages.RemoveAt(Messages.Count - 1);

        Append(new ChatMessage { Text = question, IsUser = true });
        _history.Add(("user", question));
        _lastQuestion = question;
        InputText = string.Empty;

        SetBusy(true);
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var historyForApi = _history.Take(_history.Count - 1).ToList();
            var result = await _chatService.AskTypedAsync(question, historyForApi, cts.Token);

            // Clear() (or leaving the page) cancelled this call and already reset the chat: nothing to show.
            if (cts.IsCancellationRequested || result.Kind == GeminiErrorKind.Cancelled) return;

            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.Value))
            {
                Append(new ChatMessage { Text = result.Value.Trim(), IsUser = false });
                _history.Add(("model", result.Value.Trim()));
            }
            else
            {
                // The unanswered question stays out of the model history.
                if (_history.Count > 0) _history.RemoveAt(_history.Count - 1);
                Append(new ChatMessage
                {
                    Text = AiChatService.ToUserMessage(result),
                    IsUser = false,
                    IsError = true,
                    ErrorKind = result.Kind == GeminiErrorKind.None ? GeminiErrorKind.BadResponse : result.Kind
                });
            }
        }
        catch (OperationCanceledException)
        {
            if (!cts.IsCancellationRequested && _history.Count > 0) _history.RemoveAt(_history.Count - 1);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            if (cts.IsCancellationRequested) return;
            if (_history.Count > 0) _history.RemoveAt(_history.Count - 1);
            Append(new ChatMessage { Text = "Something went wrong. Please try again.", IsUser = false, IsError = true, ErrorKind = GeminiErrorKind.BadResponse });
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            SetBusy(false);
        }
    }

    private async Task Retry()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(_lastQuestion)) return;
        // Remove the error row and the echoed user question; Send re-adds the question.
        if (Messages.Count > 0 && Messages[^1].IsError) Messages.RemoveAt(Messages.Count - 1);
        if (Messages.Count > 0 && Messages[^1].IsUser && Messages[^1].Text == _lastQuestion) Messages.RemoveAt(Messages.Count - 1);
        await Send(_lastQuestion);
    }

    private void Append(ChatMessage message)
    {
        Messages.Add(message);
        MessageAppended?.Invoke(this, message);
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        OnPropertyChanged(nameof(CanSend));
        ((Command)SendCommand).ChangeCanExecute();
    }

    private void Clear()
    {
        _cts?.Cancel();
        _history.Clear();
        Messages.Clear();
        _lastQuestion = null;
        ClearError();
    }

    /// <summary>Cancels an in-flight request when the page goes away.</summary>
    public void CancelPending() => _cts?.Cancel();
}
