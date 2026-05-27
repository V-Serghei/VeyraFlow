using System.Collections.Generic;

namespace Veyra.Infrastructure.Data.Setup.Models.Snapshots;

internal sealed record CurrentFileDigest(
    string Sha256,
    long SizeBytes,
    IReadOnlyList<string> ChunkHashes,
    BlockHashFamily HashFamily);
