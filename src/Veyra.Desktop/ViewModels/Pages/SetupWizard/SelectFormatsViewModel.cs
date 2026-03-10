using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Models.Pages.SetupWizard;
using Veyra.Desktop.Native;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed class SelectFormatsViewModel : INotifyPropertyChanged
{
    private readonly ILogger<SelectFormatsViewModel> _log;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public event EventHandler? SelectionChanged;

    public ObservableCollection<FileExtensionOption> AllExtensions { get; } = new();
    public string? CustomExt { get; set; }
    public ICommand AddCustomCommand { get; }

    public SelectFormatsViewModel(ILogger<SelectFormatsViewModel> log)
    {
        _log = log;

        foreach (string e in new[]
                 {
                     ".docx", ".pdf", ".txt", ".rtf", ".odt", ".xlsx",
                     ".png", ".jpg", ".jpeg", ".gif", ".svg",
                     ".json", ".xml", ".cs", ".js", ".ts", ".java", ".py", ".md"
                 })
            AddItem(e);

        LocalizationManager.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FooterText));
        };

        AddCustomCommand = new RelayCommand(_ => AddCustom());
    }

    public bool HasAny => AllExtensions.Any(x => x.IsSelected);
    public string FooterText => Loc.P("setup.formats_selected", AllExtensions.Count(x => x.IsSelected), AllExtensions.Count(x => x.IsSelected));

    private void AddItem(string name)
    {
        var item = new FileExtensionOption(name);
        item.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(FileExtensionOption.IsSelected))
            {
                OnPropertyChanged(nameof(HasAny));
                OnPropertyChanged(nameof(FooterText));
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        AllExtensions.Add(item);
    }

    private void AddCustom()
    {
        string e = (CustomExt ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(e)) return;
        if (!e.StartsWith('.')) e = "." + e;
        if (AllExtensions.Any(x => x.Name.Equals(e, StringComparison.OrdinalIgnoreCase))) return;

        var item = new FileExtensionOption(e) { IsSelected = true };
        item.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAny));
            OnPropertyChanged(nameof(FooterText));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
        AllExtensions.Add(item);
        CustomExt = string.Empty;
        OnPropertyChanged(nameof(CustomExt));
        OnPropertyChanged(nameof(FooterText));
    }

    public Task<bool> CommitAsync()
    {
        var selected = AllExtensions.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0)
        {
            _log.LogWarning("No formats selected");
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public IReadOnlyCollection<string> GetSelectedExtensions()
        => AllExtensions.Where(x => x.IsSelected).Select(x => x.Name).ToList();
}
