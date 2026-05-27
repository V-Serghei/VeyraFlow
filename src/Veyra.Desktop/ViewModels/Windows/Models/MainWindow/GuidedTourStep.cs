using System;
using System.Threading.Tasks;

namespace Veyra.Desktop.ViewModels.Windows;

internal sealed record GuidedTourStep(
    string TargetName,
    string TitleKey,
    string DescriptionKey,
    Func<Task<bool>>? EnterAsync = null);
