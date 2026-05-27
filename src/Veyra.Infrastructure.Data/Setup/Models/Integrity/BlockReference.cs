namespace Veyra.Infrastructure.Data.Setup.Models.Integrity;

internal sealed record BlockReference(
    string BlockHash,
    long FileVersionId,
    string RelativePath,
    int Sequence,
    int LengthBytes);
