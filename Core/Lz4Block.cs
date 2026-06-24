namespace CDTextureOverlayBuilder.Core;

public static class Lz4Block
{
    // Nexus-friendly pure C# fallback. Compress() deliberately emits a valid
    // literal-only LZ4 block. It is larger than optimal, but it avoids native
    // helpers and still decodes to identical bytes. DDS first-mip compression
    // only uses the compressed form when it is smaller, so DDS quality/parity is safe.
    public static byte[] Compress(ReadOnlySpan<byte> input)
    {
        using var ms = new MemoryStream(input.Length + input.Length / 255 + 16);
        int len = input.Length;
        int tokenLit = Math.Min(len, 15);
        ms.WriteByte((byte)(tokenLit << 4));
        if (len >= 15)
        {
            int extra = len - 15;
            while (extra >= 255) { ms.WriteByte(255); extra -= 255; }
            ms.WriteByte((byte)extra);
        }
        ms.Write(input);
        return ms.ToArray();
    }

    public static byte[] Decompress(ReadOnlySpan<byte> input, int originalSize)
    {
        byte[] output = new byte[originalSize];
        int ip = 0, op = 0;
        while (ip < input.Length)
        {
            int token = input[ip++];
            int litLen = token >> 4;
            if (litLen == 15)
            {
                int b;
                do { if (ip >= input.Length) throw new InvalidDataException("Bad LZ4 literal length"); b = input[ip++]; litLen += b; } while (b == 255);
            }
            if (ip + litLen > input.Length || op + litLen > output.Length) throw new InvalidDataException("Bad LZ4 literal copy");
            input.Slice(ip, litLen).CopyTo(output.AsSpan(op));
            ip += litLen;
            op += litLen;
            if (ip >= input.Length) break;
            if (ip + 2 > input.Length) throw new InvalidDataException("Bad LZ4 offset");
            int offset = input[ip] | (input[ip + 1] << 8);
            ip += 2;
            if (offset == 0 || offset > op) throw new InvalidDataException("Bad LZ4 match offset");
            int matchLen = token & 0x0F;
            if (matchLen == 15)
            {
                int b;
                do { if (ip >= input.Length) throw new InvalidDataException("Bad LZ4 match length"); b = input[ip++]; matchLen += b; } while (b == 255);
            }
            matchLen += 4;
            if (op + matchLen > output.Length) throw new InvalidDataException("Bad LZ4 match copy");
            for (int i = 0; i < matchLen; i++) output[op + i] = output[op - offset + i];
            op += matchLen;
        }
        if (op != originalSize) Array.Resize(ref output, op);
        return output;
    }
}
