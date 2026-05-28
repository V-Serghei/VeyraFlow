# Code Patterns

## MediatR Command (with result)

```csharp
// Command
public sealed record CreateFooCommand(int RepositoryId, string Name)
    : IRequest<OperationResult<FooDto>>;

// Validator
public sealed class CreateFooValidator : AbstractValidator<CreateFooCommand>
{
    public CreateFooValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

// Handler
public sealed class CreateFooHandler(
    IFooRepository repo,
    ILogger<CreateFooHandler> log)
    : IRequestHandler<CreateFooCommand, OperationResult<FooDto>>
{
    public async Task<OperationResult<FooDto>> Handle(
        CreateFooCommand request, CancellationToken ct)
    {
        try
        {
            var id = await repo.CreateAsync(request.Name, ct);
            return OperationResult<FooDto>.Ok(new FooDto(id, request.Name));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to create foo {Name}", request.Name);
            return OperationResult<FooDto>.Fail("Failed to create.");
        }
    }
}
```

## MediatR Query

```csharp
public sealed record GetFooQuery(int Id) : IRequest<FooDto?>;

public sealed class GetFooHandler(IFooRepository repo)
    : IRequestHandler<GetFooQuery, FooDto?>
{
    public Task<FooDto?> Handle(GetFooQuery request, CancellationToken ct)
        => repo.GetByIdAsync(request.Id, ct);
}
```

## ViewModel (CommunityToolkit.Mvvm)

```csharp
public sealed partial class FooViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string _name = string.Empty;

    public bool IsValid => !string.IsNullOrWhiteSpace(Name);

    [RelayCommand]
    private async Task SaveAsync()
    {
        var result = await _mediator.Send(new CreateFooCommand(RepositoryId, Name));
        if (!result.Success)
            ErrorMessage = result.Error;
    }
}
```

## Collapsible Section (Avalonia)

Used in retention settings section. Pattern for a clickable header with chevron:

```xml
<!-- Header row: 3 columns — title | spacer | chevron -->
<Grid ColumnDefinitions="*,Auto,Auto">
    <Button Grid.Column="0" Classes="transparent-flat"
            Command="{Binding ToggleSectionCommand}"
            HorizontalAlignment="Stretch">
        <TextBlock Text="Section Title" />
    </Button>
    <Button Grid.Column="2" Classes="transparent-flat"
            Command="{Binding ToggleSectionCommand}"
            Content="{Binding SectionChevron}" />
</Grid>

<!-- Collapsible content -->
<StackPanel Spacing="12" IsVisible="{Binding IsSectionExpanded}">
    <!-- section content -->
</StackPanel>
```

```csharp
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(SectionChevron))]
private bool _isSectionExpanded;  // false = collapsed by default

public string SectionChevron => IsSectionExpanded ? "▼" : "▶";

[RelayCommand]
private void ToggleSection() => IsSectionExpanded = !IsSectionExpanded;
```

## Retention Service — RunRetentionAsync

```csharp
// From DI: IRepositoryRetentionService
var result = await retentionService.RunRetentionAsync(
    repositoryId,
    dryRun: true,   // false to actually delete
    policyOverride: null);   // null uses repo/global effective policy

// Result fields
result.SnapshotsMarked    // number that would be (or were) deleted
result.DryRun             // echoes the param
result.PastedApplied      // whether anything was actually done
result.Summary            // human-readable text like "Applied: snapshots=1, ..."
```

Note: apply (dryRun=false) hard-deletes in the same transaction — no soft-delete record survives.

## Fake IRepositorySnapshotRepository (tests)

When creating test fakes for this large interface, stub all methods. The interface includes:
`SaveSnapshotAsync`, `ApplyWorkingSnapshotDeltaAsync`, `ApplyVersionedSnapshotDeltaAsync`,
`GetLatestEntriesAsync`, `GetLatestEntriesBatchAsync`, `GetFileVersionsAsync`,
`GetPendingChangesAsync`, `GetSnapshotHistoryAsync`, `GetSnapshotHistoryBatchAsync`,
`GetSnapshotChangedFilesAsync`, `GetPendingFileDiffPreviewAsync`, `GetFileVersionDiffPreviewAsync`,
`GetStoredTextDiffAsync`, `SaveStoredTextDiffAsync`, `GetFileVersionRestoreDataAsync`.

Use `throw new NotSupportedException()` for methods not exercised in the test.

## IFileContentStore — RestoreFileAsync signature

```csharp
Task<long> RestoreFileAsync(
    IReadOnlyList<StoredFileBlockDto> blocks,
    string targetPath,
    bool overwriteExisting,
    string? expectedContentHash = null,  // ← pass to get post-restore hash validation
    CancellationToken ct = default);
```

Pass `expectedContentHash` from `FileVersionRestoreDto.ContentHashSha256` in restore flows.
Pass `null` in diff/preview flows (temp files, no user-facing integrity check needed).
