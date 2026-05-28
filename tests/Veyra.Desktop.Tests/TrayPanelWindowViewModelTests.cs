using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Tests;

public sealed class TrayPanelWindowViewModelTests
{
    [Fact]
    public void ApplyState_SuppressesProgrammaticSelectionChanges_ButKeepsUserSelectionEvents()
    {
        var viewModel = new TrayPanelWindowViewModel();
        var eventCount = 0;
        int? lastSelectedRepositoryId = null;

        viewModel.SelectedRepositoryChanged += repositoryId =>
        {
            eventCount++;
            lastSelectedRepositoryId = repositoryId;
        };

        viewModel.ApplyState(
            "Ready",
            "Connected",
            "#4ADE80",
            "#164ADE80",
            "Pause",
            hasCloudAccess: true,
            canRunCloudActions: true,
            cloudHintText: null,
            repositories:
            [
                new TrayPanelRepositoryOptionViewModel(1, "Alpha"),
                new TrayPanelRepositoryOptionViewModel(2, "Beta")
            ],
            selectedRepositoryId: 1,
            lastUpdatedText: "just now",
            currentActivityText: null,
            currentActivityAccentColor: "#6EA8FF",
            currentActivityBackgroundColor: "#1A6EA8FF",
            processLoadText: null,
            processLoadDetailText: null,
            processLoadPeakText: null,
            processLoadHistoryText: null,
            processLoadAccentColor: "#6EA8FF",
            processLoadBackgroundColor: "#1A6EA8FF",
            selectedRepositoryState: BuildRepositoryState("Alpha"),
            recentActions: Array.Empty<TrayPanelRecentActionViewModel>(),
            issueItems: Array.Empty<TrayPanelIssueItemViewModel>());

        Assert.Equal(0, eventCount);
        Assert.Equal(1, viewModel.SelectedRepository?.Id);

        viewModel.SelectedRepository = viewModel.Repositories.Single(repository => repository.Id == 2);

        Assert.Equal(1, eventCount);
        Assert.Equal(2, lastSelectedRepositoryId);

        viewModel.ApplyState(
            "Ready",
            "Connected",
            "#4ADE80",
            "#164ADE80",
            "Pause",
            hasCloudAccess: true,
            canRunCloudActions: true,
            cloudHintText: null,
            repositories:
            [
                new TrayPanelRepositoryOptionViewModel(1, "Alpha"),
                new TrayPanelRepositoryOptionViewModel(2, "Beta")
            ],
            selectedRepositoryId: 2,
            lastUpdatedText: "just now",
            currentActivityText: null,
            currentActivityAccentColor: "#6EA8FF",
            currentActivityBackgroundColor: "#1A6EA8FF",
            processLoadText: null,
            processLoadDetailText: null,
            processLoadPeakText: null,
            processLoadHistoryText: null,
            processLoadAccentColor: "#6EA8FF",
            processLoadBackgroundColor: "#1A6EA8FF",
            selectedRepositoryState: BuildRepositoryState("Beta"),
            recentActions: Array.Empty<TrayPanelRecentActionViewModel>(),
            issueItems: Array.Empty<TrayPanelIssueItemViewModel>());

        Assert.Equal(1, eventCount);
        Assert.Equal(2, viewModel.SelectedRepository?.Id);
        Assert.Equal("Beta", viewModel.SelectedRepositoryName);
    }

    private static TrayPanelRepositoryState BuildRepositoryState(string name)
    {
        return new TrayPanelRepositoryState(
            name,
            $@"C:\Repos\{name}",
            "metrics",
            "snapshot",
            "\uE823",
            "#6EA8FF",
            "live",
            "\uE895",
            "#6EA8FF",
            "live detail",
            "cloud",
            "\uE753",
            "#6EA8FF",
            "queue",
            "\uE895",
            "#6EA8FF",
            progressValue: 0d,
            progressMaximum: 1d,
            progressText: string.Empty,
            showProgress: false,
            progressAccentColor: "#6EA8FF");
    }
}
