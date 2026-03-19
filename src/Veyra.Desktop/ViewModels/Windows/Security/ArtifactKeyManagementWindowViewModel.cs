using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.ViewModels.Pages.Settings;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class ArtifactKeyManagementWindowViewModel : ObservableObject
{
    private readonly AppSettingsViewModel _source;

    public ArtifactKeyManagementWindowViewModel(AppSettingsViewModel source)
    {
        _source = source;
        _source.PropertyChanged += OnSourcePropertyChanged;
        _source.ArtifactKeys.CollectionChanged += OnArtifactKeysChanged;
    }

    public event Action? RequestClose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevokeSelectedKey))]
    [NotifyPropertyChangedFor(nameof(HasSelectedKey))]
    private AppArtifactKeyItemViewModel? _selectedKey;

    [ObservableProperty] private string _rotationNote = string.Empty;
    [ObservableProperty] private string _revocationNote = string.Empty;

    public AppSettingsViewModel Source => _source;
    public bool HasSelectedKey => SelectedKey is not null;
    public bool CanRevokeSelectedKey => SelectedKey?.CanRevoke == true && !_source.IsArtifactEncryptionBusy;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _source.ReloadArtifactEncryptionStateAsync();
        OnPropertyChanged(nameof(CanRevokeSelectedKey));
    }

    [RelayCommand]
    private async Task RotateAsync()
    {
        await _source.RotateArtifactKeyWithNoteAsync(RotationNote);
        RotationNote = string.Empty;
        OnPropertyChanged(nameof(CanRevokeSelectedKey));
    }

    [RelayCommand]
    private async Task RevokeSelectedAsync()
    {
        if (SelectedKey is null)
            return;

        await _source.RevokeArtifactKeyWithNoteAsync(SelectedKey, RevocationNote);
        RevocationNote = string.Empty;
        SelectedKey = null;
        OnPropertyChanged(nameof(CanRevokeSelectedKey));
    }

    [RelayCommand]
    private void Close()
    {
        Detach();
        RequestClose?.Invoke();
    }

    private void OnArtifactKeysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SelectedKey is not null)
        {
            SelectedKey = _source.ArtifactKeys.FirstOrDefault(key =>
                string.Equals(key.KeyId, SelectedKey.KeyId, StringComparison.OrdinalIgnoreCase));
        }

        OnPropertyChanged(nameof(CanRevokeSelectedKey));
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettingsViewModel.IsArtifactEncryptionBusy)
            or nameof(AppSettingsViewModel.HasArtifactKeys)
            or nameof(AppSettingsViewModel.ArtifactActiveKeyCount)
            or nameof(AppSettingsViewModel.ArtifactRetiredKeyCount)
            or nameof(AppSettingsViewModel.ArtifactRevokedKeyCount))
        {
            OnPropertyChanged(nameof(CanRevokeSelectedKey));
        }
    }

    public void Detach()
    {
        _source.PropertyChanged -= OnSourcePropertyChanged;
        _source.ArtifactKeys.CollectionChanged -= OnArtifactKeysChanged;
    }
}
