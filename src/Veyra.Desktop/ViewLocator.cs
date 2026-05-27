using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Veyra.Desktop;

public class ViewLocator : IDataTemplate
{

    public Control? Build(object? param)
    {
        if (param is null) return null;

        var vmType = param.GetType();
        // 1) ViewModels → Views
        // 2) ViewModel → View
        var viewFullName = vmType.FullName!
            .Replace(".ViewModels.", ".Views.", StringComparison.Ordinal)
            .Replace("ViewModel", "View", StringComparison.Ordinal);

        var asmName = vmType.Assembly.GetName().Name;
        var qualifiedName = $"{viewFullName}, {asmName}";

        var viewType = Type.GetType(qualifiedName);
        if (viewType is null)
            return new TextBlock { Text = $"Not Found: {viewFullName}" };

        try
        {
            var provider = App._serviceProvider;
            if (provider is not null)
                return (Control)ActivatorUtilities.CreateInstance(provider, viewType);

            return (Control)Activator.CreateInstance(viewType)!;
        }
        catch
        {
            return (Control)Activator.CreateInstance(viewType)!;
        }
    }

    public bool Match(object? data) => data is ObservableObject;
}

