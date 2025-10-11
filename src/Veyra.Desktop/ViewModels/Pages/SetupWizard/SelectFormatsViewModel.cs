using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Setup;
using Veyra.Desktop.Native;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed class SelectFormatsViewModel
{
    public sealed class ExtItem
    {
        public ExtItem(string name) { Name = name; }
        public string Name { get; }
        public bool IsSelected { get; set; }
    }

    private readonly IMediator _mediator;
    private readonly ILogger<SelectFormatsViewModel> _log;

    public ObservableCollection<ExtItem> AllExtensions { get; } = new();
    public string? CustomExt { get; set; }
    public ICommand AddCustomCommand { get; }

    public SelectFormatsViewModel(IMediator mediator, ILogger<SelectFormatsViewModel> log)
    {
        _mediator = mediator; _log = log;

        foreach (var e in new[] { ".docx",".pdf",".txt",".rtf",".odt",".xlsx",".png",".jpg",".jpeg",".gif",".svg",".json",".xml",".cs",".js",".ts",".java",".py",".md" })
            AllExtensions.Add(new ExtItem(e));

        AddCustomCommand = new RelayCommand(_ => AddCustom());
    }

    private void AddCustom()
    {
        var e = (CustomExt ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(e)) return;
        if (!e.StartsWith(".")) e = "." + e;
        if (AllExtensions.Any(x => x.Name.Equals(e, System.StringComparison.OrdinalIgnoreCase))) return;

        AllExtensions.Add(new ExtItem(e) { IsSelected = true });
        CustomExt = string.Empty;
    }

    public string FooterText => $"{AllExtensions.Count(x => x.IsSelected)} формат(а/ов) выбрано";
    public bool HasAny => AllExtensions.Any(x => x.IsSelected);

    public async Task<bool> CommitAsync()
    {
        var selected = AllExtensions.Where(x => x.IsSelected).Select(x => x.Name).ToList();
        var cmd = new SetTrackedExtensionsCommand(selected);

        try { await _mediator.Send(cmd); return true; }
        catch (System.Exception ex) { _log.LogError(ex, "Failed to save tracked extensions."); return false; }
    }
}
