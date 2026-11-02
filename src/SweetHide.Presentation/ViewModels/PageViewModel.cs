using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using SweetHide.Presentation.Services;

namespace SweetHide.Presentation.ViewModels;

public enum StatusSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>Shared busy flag and status banner (an InfoBar in the UI) for every page.</summary>
public abstract partial class PageViewModel(IShellService shell) : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _statusIsOpen;

    [ObservableProperty]
    private StatusSeverity _statusSeverity;

    [ObservableProperty]
    private string _statusTitle = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusAction))]
    private string? _statusActionPath;

    public bool HasStatusAction => StatusActionPath is not null;

    protected void ShowStatus(StatusSeverity severity, string title, string message, string? actionPath = null)
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusMessage = message;
        StatusActionPath = actionPath;
        StatusIsOpen = true;
    }

    protected void ClearStatus()
    {
        StatusIsOpen = false;
        StatusActionPath = null;
    }

    protected virtual void OnBusyChanged()
    {
    }

    partial void OnIsBusyChanged(bool value) => OnBusyChanged();

    [RelayCommand]
    private void ShowInFolder()
    {
        if (StatusActionPath is { } path)
        {
            shell.ShowInFolder(path);
        }
    }
}
