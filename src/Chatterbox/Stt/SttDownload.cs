using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Chatterbox.Stt;

// Shared by the model and pack downloads: a stalled connection fails after
// IdleTimeoutMs without a byte instead of hanging forever behind a Cancel
// button, and a download is refused up front when the disk can't hold it.
public static class SttDownload
{
    public const int IdleTimeoutMs = 60_000;

    // Copies source to target while hashing, reporting cumulative bytes.
    public static async Task<long> CopyAsync(Stream source, Stream target, SHA256 sha,
        Action<long> progress, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        long received = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeoutMs);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"no data received for {IdleTimeoutMs / 1000} s — check the connection and try again");
            }
            if (read <= 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            sha.TransformBlock(buffer, 0, read, null, 0);
            received += read;
            progress(received);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return received;
    }

    // Throws an IOException in plain words when the target drive lacks room.
    public static void EnsureFreeSpace(string directory, long bytesNeeded)
    {
        string root;
        long free;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(directory)) ?? "";
            if (root.Length == 0) return;
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return; } // unusual drive layout — let the download try
        if (free < bytesNeeded)
            throw new IOException(
                $"not enough space on drive {root.TrimEnd('\\', '/')}: needs {bytesNeeded / 1_000_000} MB free, has {free / 1_000_000} MB");
    }
}
