using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Setup;
using Veyra.Desktop.Models.Pages.SetupWizard;
using Veyra.Desktop.Native;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed class SelectFormatsViewModel : INotifyPropertyChanged
{
    private readonly IMediator _mediator;
    private readonly ILogger<SelectFormatsViewModel> _log;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public event EventHandler? SelectionChanged;

    public ObservableCollection<FileExtensionOption> AllExtensions { get; } = new();
    public string? CustomExt { get; set; }
    public ICommand AddCustomCommand { get; }

    /// <summary>
    /// Constructor for SelectFormatsViewModel.
    /// This view model allows users to select and manage a list of file formats/extensions.
    /// </summary>
    /// <param name="mediator"></param>
    /// <param name="log"></param>
    public SelectFormatsViewModel(IMediator mediator, ILogger<SelectFormatsViewModel> log)
    {
        _mediator = mediator; _log = log;

        foreach (string e in new[] { ".docx",".pdf",".txt",".rtf",".odt",".xlsx",".png",".jpg",".jpeg",".gif",".svg",".json",".xml",".cs",".js",".ts",".java",".py",".md" })
            AddItem(e);

        AddCustomCommand = new RelayCommand(_ => AddCustom());
    }

    /// <summary>
    /// HasAny indicates if any file extensions are currently selected.
    /// </summary>
    public bool HasAny => AllExtensions.Any(x => x.IsSelected);

    /// <summary>
    /// FooterText provides a summary of how many formats are selected.
    /// </summary>
    public string FooterText => $"{AllExtensions.Count(x => x.IsSelected)} format(s) selected";


    /// <summary>
    /// Adds a new file extension option to the list.
    /// If the extension already exists, it is not added again.
    /// </summary>
    /// <param name="name"></param>
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

    /// <summary>
    /// Adds a custom file extension entered by the user.
    /// Validates the input and ensures no duplicates are added.
    /// </summary>
    private void AddCustom()
    {
        string e = (CustomExt ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(e)) return;
        if (!e.StartsWith(".")) e = "." + e;
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
        OnPropertyChanged(nameof(FooterText));
    }



    /// <summary>
    /// Commits the selected file extensions by sending a SetTrackedExtensionsCommand via MediatR.
    /// Returns true if the operation is successful, false otherwise.
    /// </summary>
    /// <returns></returns>
    public async Task<bool> CommitAsync()
    {
        var selected = AllExtensions.Where(x => x.IsSelected).Select(x => x.Name).ToList();
        try
        {
            await _mediator.Send(new SetTrackedExtensionsCommand(selected));
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save tracked extensions.");
            return false;
        }
    }
}
