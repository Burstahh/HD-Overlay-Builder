using System.Buffers.Binary;
using System.Text;

namespace CDTextureOverlayBuilder.Core;

internal static class BinaryUtil
{
    public static uint U32(byte[] data, int off) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off, 4));
    public static ushort U16(byte[] data, int off) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off, 2));
    public static void W32(Span<byte> data, int off, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(off, 4), value);
    public static void W16(Span<byte> data, int off, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(off, 2), value);
    public static byte[] PackU32(params uint[] values)
    {
        var b = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) W32(b, i * 4, values[i]);
        return b;
    }
    public static byte[] PackU16(params ushort[] values)
    {
        var b = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) W16(b, i * 2, values[i]);
        return b;
    }
    public static string Utf8(byte[] data, int off, int len) => Encoding.UTF8.GetString(data, off, len);
    public static string Ascii(byte[] data, int off, int len) => Encoding.ASCII.GetString(data, off, len);
    public static void WriteU32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        s.Write(b);
    }
    public static void WriteU16(Stream s, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        s.Write(b);
    }
}
