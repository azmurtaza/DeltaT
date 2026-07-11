using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaT.App.Services;

namespace DeltaT.App.ViewModels;

public partial class FeedbackViewModel : ObservableObject
{
    private readonly FeedbackService _service;

    // compose | sending | done
    [ObservableProperty] private string _state = "compose";
    [ObservableProperty] private bool _isBug = true;
    [ObservableProperty] private bool _isIdea;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _contact = "";
    [ObservableProperty] private string _statusText = "";

    public FeedbackViewModel(FeedbackService service) => _service = service;

    // Mutually exclusive kind toggle, without a converter.
    partial void OnIsBugChanged(bool value)
    {
        if (value) IsIdea = false;
        else if (!IsIdea) IsBug = true; // never leave both off
    }

    partial void OnIsIdeaChanged(bool value)
    {
        if (value) IsBug = false;
        else if (!IsBug) IsIdea = true;
    }

    partial void OnMessageChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    private bool CanSend() => State != "sending" && !string.IsNullOrWhiteSpace(Message);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        string kind = IsIdea ? "idea" : "bug";
        State = "sending";
        StatusText = "Sending…";
        SendCommand.NotifyCanExecuteChanged();

        (bool ok, string? error) = await _service.SendAsync(kind, Message.Trim(), Contact);

        if (ok)
        {
            State = "done";
            StatusText = "";
        }
        else
        {
            State = "compose";
            StatusText = $"Could not send ({error}). Check your connection and try again.";
        }
        SendCommand.NotifyCanExecuteChanged();
    }
}
