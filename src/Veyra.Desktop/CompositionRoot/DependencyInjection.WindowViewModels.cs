using Microsoft.Extensions.DependencyInjection;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.CompositionRoot;

public static partial class DependencyInjection
{
    private static IServiceCollection AddDesktopWindowViewModels(this IServiceCollection services)
    {
        services.AddTransient<WelcomeWindowViewModel>();
        services.AddTransient<CreateRepositoryWindowViewModel>();
        services.AddTransient<SnapshotNameDialogWindowViewModel>();
        services.AddTransient<FileVersionCompareWindowViewModel>();
        services.AddTransient<ConfirmActionWindowViewModel>();
        services.AddTransient<PasswordVerificationWindowViewModel>();
        services.AddTransient<AuthDialogWindowViewModel>();
        services.AddTransient<RepositoryRetentionWizardWindowViewModel>();
        services.AddTransient<OperationJournalWindowViewModel>();
        services.AddTransient<OperationMonitorWindowViewModel>();
        services.AddTransient<RepositoryBundleExportWizardWindowViewModel>();
        services.AddTransient<RepositoryBundleImportWizardWindowViewModel>();
        services.AddTransient<TrayPanelWindowViewModel>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<InfoWindowViewModel>();
        return services;
    }
}
