using System.Collections.Generic;

namespace Veyra.Desktop.Services.Auth.Models;

internal sealed class LocalCredentialFileModel
{
    public List<LocalCredentialEntry> Entries { get; init; } = [];
}
