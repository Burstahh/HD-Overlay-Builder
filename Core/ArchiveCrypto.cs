using System.Buffers.Binary;
using System.Text;

namespace CDTextureOverlayBuilder.Core;

public static class ArchiveCrypto
{
    private const uint HashInitVal = 0x000C5EDE;
    private const uint IvXor = 0x60616263;
    private static readonly uint[] XorDeltas = { 0x00000000, 0x0A0A0A0A, 0x0C0C0C0C, 0x06060606, 0x0E0E0E0E, 0x0A0A0A0A, 0x06060606, 0x02020202 };

    public static (byte[] key, byte[] iv) DeriveKeyIv(string filename)
    {
        string basename = Path.GetFileName(filename).ToLowerInvariant();
        uint seed = HashLittle.Compute(Encoding.UTF8.GetBytes(basename), HashInitVal);
        byte[] iv = new byte[16];
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(iv.AsSpan(i * 4), seed);
        uint keyBase = seed ^ IvXor;
        byte[] key = new byte[32];
        for (int i = 0; i < XorDeltas.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(i * 4), keyBase ^ XorDeltas[i]);
        return (key, iv);
    }

    public static byte[] EncryptDecrypt(ReadOnlySpan<byte> data, string filename)
    {
        var (key, iv) = DeriveKeyIv(filename);
        byte[] output = data.ToArray();
        ChaCha20Xor(output, key, iv);
        return output;
    }

    // Python cryptography.algorithms.ChaCha20 uses the original 64-bit counter + 64-bit nonce shape in a 16-byte nonce.
    private static void ChaCha20Xor(byte[] data, byte[] key, byte[] iv)
    {
        uint[] state = new uint[16];
        state[0] = 0x61707865; state[1] = 0x3320646e; state[2] = 0x79622d32; state[3] = 0x6b206574;
        for (int i = 0; i < 8; i++) state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4));
        state[12] = BinaryPrimitives.ReadUInt32LittleEndian(iv.AsSpan(0));
        state[13] = BinaryPrimitives.ReadUInt32LittleEndian(iv.AsSpan(4));
        state[14] = BinaryPrimitives.ReadUInt32LittleEndian(iv.AsSpan(8));
        state[15] = BinaryPrimitives.ReadUInt32LittleEndian(iv.AsSpan(12));

        byte[] block = new byte[64];
        int offset = 0;
        while (offset < data.Length)
        {
            uint[] working = (uint[])state.Clone();
            for (int i = 0; i < 10; i++)
            {
                Quarter(working, 0, 4, 8, 12); Quarter(working, 1, 5, 9, 13); Quarter(working, 2, 6, 10, 14); Quarter(working, 3, 7, 11, 15);
                Quarter(working, 0, 5, 10, 15); Quarter(working, 1, 6, 11, 12); Quarter(working, 2, 7, 8, 13); Quarter(working, 3, 4, 9, 14);
            }
            for (int i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(i * 4), working[i] + state[i]);
            int n = Math.Min(64, data.Length - offset);
            for (int i = 0; i < n; i++) data[offset + i] ^= block[i];
            offset += n;
            state[12]++;
            if (state[12] == 0) state[13]++;
        }
    }

    private static uint Rot(uint v, int c) => (v << c) | (v >> (32 - c));
    private static void Quarter(uint[] x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] = Rot(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = Rot(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = Rot(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = Rot(x[b] ^ x[c], 7);
    }
}
