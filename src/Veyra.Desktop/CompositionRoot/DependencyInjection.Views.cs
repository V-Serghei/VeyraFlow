using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Desktop.ViewModels.Pages.WelcomeWindow;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views;
using Veyra.Desktop.Views.Pages.SetupWizard;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.CompositionRoot;

public static partial class DependencyInjection
{
    private static IServiceCollection AddDesktopViews(this IServiceCollection services)
    {
        services.AddWindowWithDataContext<WelcomeWindow, WelcomeWindowViewModel>();
        services.AddWindowWithDataContext<MainWindow, MainWindowViewModel>();
        services.AddWindowWithDataContext<InfoWindow, InfoWindowViewModel>();
        services.AddWindowWithDataContext<CloudInformationWindow, CloudInformationWindowViewModel>();
        services.AddWindowWithDataContext<CloudRepositoryManagerWindow, CloudRepositoryManagerWindowViewModel>();
        services.AddWindowWithDataContext<CloudRestoreWizardWindow, CloudRestoreWizardWindowViewModel>();
        services.AddWindowWithDataContext<CreateRepositoryWindow, CreateRepositoryWindowViewModel>();
        services.AddWindowWithDataContext<SnapshotNameDialogWindow, SnapshotNameDialogWindowViewModel>();
        services.AddWindowWithDataContext<FileVersionCompareWindow, FileVersionCompareWindowViewModel>();
        services.AddWindowWithDataContext<ConfirmActionWindow, ConfirmActionWindowViewModel>();
        services.AddWindowWithDataContext<PasswordVerificationWindow, PasswordVerificationWindowViewModel>();
        services.AddWindowWithDataContext<AuthDialogWindow, AuthDialogWindowViewModel>();
        services.AddWindowWithDataContext<RepositoryRetentionWizardWindow, RepositoryRetentionWizardWindowViewModel>();
        services.AddWindowWithDataContext<OperationJournalWindow, OperationJournalWindowViewModel>();
        services.AddWindowWithDataContext<OperationMonitorWindow, OperationMonitorWindowViewModel>();
        services.AddWindowWithDataContext<RepositoryBundleExportWizardWindow, RepositoryBundleExportWizardWindowViewModel>();
        services.AddWindowWithDataContext<RepositoryBundleImportWizardWindow, RepositoryBundleImportWizardWindowViewModel>();
        services.AddWindowWithDataContext<TrayPanelWindow, TrayPanelWindowViewModel>();
        services.AddWindowWithDataContext<RepositoryFolderPickerWindow, RepositoryFolderPickerWindowViewModel>();

        services.AddTransient<CloudSyncHealthWindow>();
        services.AddTransient<SnapshotHistoryWindow>();
        services.AddTransient<SetupWizardWindow>();
        return services;
    }

    private static IServiceCollection AddWindowWithDataContext<TWindow, TViewModel>(this IServiceCollection services)
        where TWindow : Window, new()
        where TViewModel : class
    {
        services.AddTransient<TWindow>(sp =>
            new TWindow { DataContext = sp.GetRequiredService<TViewModel>() });
        return services;
    }
}
