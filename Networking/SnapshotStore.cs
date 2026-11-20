using System.Security.Cryptography;

namespace TurboTick.Networking;

/// <summary>Immutable, content-addressed snapshots. Publishing latest is the final atomic operation.</summary>
public sealed class SnapshotStore
{
    private readonly string _directory;
    private readonly string _objects;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private sealed record LatestPointer(string Id);

    public SnapshotStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _objects = Path.Combine(_directory, "objects");
        Directory.CreateDirectory(_objects);
    }

    public async Task<SnapshotManifest> PutAsync(Stream source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The snapshot source must be readable.", nameof(source));
        var temporary = Path.Combine(_objects, ".upload-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var wholeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var hashes = new List<string>();
            long length = 0;
            var buffer = new byte[NetworkProtocol.SnapshotChunkSize];
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var count = 0;
                    while (count < buffer.Length)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(count), ct).ConfigureAwait(false);
                        if (read == 0) break;
                        count += read;
                    }
                    if (count == 0) break;
                    var chunk = buffer.AsMemory(0, count);
                    await destination.WriteAsync(chunk, ct).ConfigureAwait(false);
                    wholeHash.AppendData(chunk.Span);
                    hashes.Add(Convert.ToHexStringLower(SHA256.HashData(chunk.Span)));
                    length = checked(length + count);
                    if (count != buffer.Length) break;
                }
                await destination.FlushAsync(ct).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }
            var manifest = new SnapshotManifest(Convert.ToHexStringLower(wholeHash.GetHashAndReset()), length,
                NetworkProtocol.SnapshotChunkSize, hashes.ToArray());
            await _publishGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                // Blob first, then its manifest, then the latest pointer. A crash leaves only an unreferenced object.
                File.Move(temporary, BlobPath(manifest.Id), overwrite: true);
                await AtomicFile.WriteJsonAsync(ManifestPath(manifest.Id), manifest, ct).ConfigureAwait(false);
                await AtomicFile.WriteJsonAsync(Path.Combine(_directory, "latest.json"), new LatestPointer(manifest.Id), ct)
                    .ConfigureAwait(false);
            }
            finally { _publishGate.Release(); }
            return manifest;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<SnapshotManifest?> GetLatestAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.Combine(_directory, "latest.json");
        if (!File.Exists(path)) return null;
        var pointer = await AtomicFile.ReadJsonAsync<LatestPointer>(path, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The latest snapshot pointer is empty.");
        return await GetManifestAsync(pointer.Id, ct).ConfigureAwait(false);
    }

    public async Task<SnapshotChunk> ReadChunkAsync(string id, int index, CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(id, ct).ConfigureAwait(false);
        var length = Validation.ChunkLength(manifest, index);
        var data = GC.AllocateUninitializedArray<byte>(length);
        await using (var stream = new FileStream(BlobPath(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            stream.Seek((long)index * manifest.ChunkSize, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(data));
        if (!string.Equals(hash, manifest.ChunkHashes[index], StringComparison.Ordinal))
            throw new InvalidDataException("The stored snapshot chunk failed SHA256 verification.");
        return new SnapshotChunk(id, index, data, hash);
    }

    /// <summary>整份快照读进内存，供基线下发切片使用。
    ///
    /// 一次 18 MB：服务端把同一份存档推给每个新副本，切片比每次开流便宜；调用方按 id 缓存，
    /// 房间换代时自然失效。读之前先校验长度与索引，避免把半截文件当成存档推出去。</summary>
    public async Task<byte[]> ReadAllAsync(string id, CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(id, ct).ConfigureAwait(false);
        var data = GC.AllocateUninitializedArray<byte>(checked((int)manifest.Length));
        await using (var stream = new FileStream(BlobPath(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        var hash = Convert.ToHexStringLower(SHA256.HashData(data));
        if (!string.Equals(hash, manifest.Id, StringComparison.Ordinal))
            throw new InvalidDataException("The stored snapshot failed SHA256 verification.");
        return data;
    }

    private async Task<SnapshotManifest> GetManifestAsync(string id, CancellationToken ct)
    {
        Validation.ContentId(id);
        var manifest = await AtomicFile.ReadJsonAsync<SnapshotManifest>(ManifestPath(id), ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The stored snapshot manifest is empty.");
        Validation.Manifest(manifest);
        if (!string.Equals(manifest.Id, id, StringComparison.Ordinal))
            throw new InvalidDataException("The snapshot manifest id does not match its filename.");
        var info = new FileInfo(BlobPath(id));
        if (!info.Exists || info.Length != manifest.Length)
            throw new InvalidDataException("The snapshot blob is missing or has an unexpected length.");
        return manifest;
    }

    private string BlobPath(string id) => Path.Combine(_objects, id + ".blob");
    private string ManifestPath(string id) => Path.Combine(_objects, id + ".json");
}
