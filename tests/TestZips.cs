/** Crafted zips: what an attacker sends rather than what ZipArchive writes. */
internal static class TestZips
{
    /** Rewrites the uncompressed size in every local header and central directory record — the zip's own claim. */
    public static void DeclareUncompressedSize(string path, uint size)
    {
        var bytes = File.ReadAllBytes(path);
        var value = BitConverter.GetBytes(size);
        for (int i = 0; i + 4 <= bytes.Length; i++)
        {
            if (bytes[i] != 0x50 || bytes[i + 1] != 0x4B) continue;
            if (bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04) value.CopyTo(bytes, i + 22); // local file header
            if (bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02) value.CopyTo(bytes, i + 24); // central directory
        }
        File.WriteAllBytes(path, bytes);
    }
}
