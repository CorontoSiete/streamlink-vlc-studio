namespace StreamStudio.Io;

/// <summary>Reads a bounded snapshot from one file handle while allowing atomic replacement.</summary>
internal static class BoundedFile
{
    internal static byte[] ReadAllBytes(string path, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumBytes, 0);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var length = stream.Length;
        if (length <= 0 || length > maximumBytes)
        {
            throw new InvalidDataException($"File exceeds the supported size or is empty: {path}");
        }

        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal static string ReadAllText(string path, int maximumBytes)
    {
        using var stream = new MemoryStream(ReadAllBytes(path, maximumBytes), writable: false);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
