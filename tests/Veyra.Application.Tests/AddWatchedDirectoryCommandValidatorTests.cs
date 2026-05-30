using Veyra.Application.Commands.Setup;

namespace Veyra.Application.Tests;

public sealed class AddWatchedDirectoryCommandValidatorTests
{
    private readonly AddWatchedDirectoryCommandValidator _validator = new();

    [Fact]
    public void Validate_Fails_WhenPathIsEmpty()
    {
        var result = _validator.Validate(new AddWatchedDirectoryCommand(string.Empty));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(AddWatchedDirectoryCommand.Path));
    }

    [Fact]
    public void Validate_Fails_WhenPathIsRootDrive()
    {
        var rootPath = Path.GetPathRoot(Environment.CurrentDirectory)!;

        var result = _validator.Validate(new AddWatchedDirectoryCommand(rootPath));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorMessage.Contains("Root drives", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Succeeds_ForExistingDirectory()
    {
        var directory = Directory.CreateTempSubdirectory();

        try
        {
            var result = _validator.Validate(new AddWatchedDirectoryCommand(directory.FullName));

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(static error => error.ErrorMessage)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
