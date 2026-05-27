using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Models.TrackedFormats;
using Veyra.Desktop.Native;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed class SelectFormatsViewModel : INotifyPropertyChanged
{
    private readonly ILogger<SelectFormatsViewModel> _log;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public event EventHandler? SelectionChanged;

    public ObservableCollection<FileExtensionOption> AllExtensions { get; } = new();
    public ObservableCollection<FormatCategoryItemViewModel> FormatCategories { get; } = new();
    public string? CustomExt { get; set; }
    public ICommand AddCustomCommand { get; }
    public ICommand ToggleCategoryCommand { get; }

    public SelectFormatsViewModel(ILogger<SelectFormatsViewModel> log)
    {
        _log = log;

        foreach (string e in TrackedFormatCategoryCatalog.All
                     .SelectMany(category => category.Formats)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            AddItem(e);

        foreach (var category in TrackedFormatCategoryCatalog.All)
            FormatCategories.Add(new FormatCategoryItemViewModel(category));

        LocalizationManager.Instance.LanguageChanged += (_, _) =>
        {
            foreach (var category in FormatCategories)
                category.RefreshLocalization();
            OnPropertyChanged(nameof(FooterText));
        };

        AddCustomCommand = new RelayCommand(_ => AddCustom());
        ToggleCategoryCommand = new RelayCommand(category => ToggleCategory(category as FormatCategoryItemViewModel));
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
                RefreshCategoryState();
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

    private void ToggleCategory(FormatCategoryItemViewModel? category)
    {
        if (category is null)
            return;

        category.IsApplied = !category.IsApplied;

        if (category.IsApplied)
        {
            foreach (var format in category.Formats)
            {
                var option = AllExtensions.FirstOrDefault(x => x.Name.Equals(format, StringComparison.OrdinalIgnoreCase));
                if (option is null)
                {
                    AddItem(format);
                    option = AllExtensions.FirstOrDefault(x => x.Name.Equals(format, StringComparison.OrdinalIgnoreCase));
                }

                if (option is not null)
                    option.IsSelected = true;
            }
        }
        else
        {
            var protectedFormats = FormatCategories
                .Where(item => !ReferenceEquals(item, category) && item.IsApplied)
                .SelectMany(item => item.Formats)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var format in category.Formats)
            {
                if (protectedFormats.Contains(format))
                    continue;

                var option = AllExtensions.FirstOrDefault(x => x.Name.Equals(format, StringComparison.OrdinalIgnoreCase));
                if (option is not null)
                    option.IsSelected = false;
            }
        }

        OnPropertyChanged(nameof(HasAny));
        OnPropertyChanged(nameof(FooterText));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshCategoryState()
    {
        var selected = AllExtensions
            .Where(option => option.IsSelected)
            .Select(option => option.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var category in FormatCategories)
        {
            var shouldBeApplied = category.Formats.All(selected.Contains);
            if (category.IsApplied != shouldBeApplied)
                category.IsApplied = shouldBeApplied;
        }
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
