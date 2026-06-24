using System.Buffers.Binary;

namespace CDTextureOverlayBuilder.Core;

public static class HashLittle
{
    public const uint IntegritySeed = 0x000C5EDE;

    private static uint Rot(uint x, int k) => (x << k) | (x >> (32 - k));

    private static void Mix(ref uint a, ref uint b, ref uint c)
    {
        a -= c; a ^= Rot(c, 4); c += b;
        b -= a; b ^= Rot(a, 6); a += c;
        c -= b; c ^= Rot(b, 8); b += a;
        a -= c; a ^= Rot(c,16); c += b;
        b -= a; b ^= Rot(a,19); a += c;
        c -= b; c ^= Rot(b, 4); b += a;
    }

    private static void Final(ref uint a, ref uint b, ref uint c)
    {
        c ^= b; c -= Rot(b,14);
        a ^= c; a -= Rot(c,11);
        b ^= a; b -= Rot(a,25);
        c ^= b; c -= Rot(b,16);
        a ^= c; a -= Rot(c,4);
        b ^= a; b -= Rot(a,14);
        c ^= b; c -= Rot(b,24);
    }

    public static uint Compute(ReadOnlySpan<byte> data, uint initval = 0)
    {
        uint len = (uint)data.Length;
        uint a = 0xDEADBEEF + len + initval;
        uint b = a;
        uint c = a;
        int offset = 0;
        int length = data.Length;
        while (length > 12)
        {
            a += BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
            b += BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            c += BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 8, 4));
            Mix(ref a, ref b, ref c);
            offset += 12;
            length -= 12;
        }
        if (length > 0)
        {
            Span<byte> tail = stackalloc byte[12];
            data.Slice(offset, length).CopyTo(tail);
            if (length >= 1) a += tail[0];
            if (length >= 2) a += (uint)tail[1] << 8;
            if (length >= 3) a += (uint)tail[2] << 16;
            if (length >= 4) a += (uint)tail[3] << 24;
            if (length >= 5) b += tail[4];
            if (length >= 6) b += (uint)tail[5] << 8;
            if (length >= 7) b += (uint)tail[6] << 16;
            if (length >= 8) b += (uint)tail[7] << 24;
            if (length >= 9) c += tail[8];
            if (length >= 10) c += (uint)tail[9] << 8;
            if (length >= 11) c += (uint)tail[10] << 16;
            if (length >= 12) c += (uint)tail[11] << 24;
            Final(ref a, ref b, ref c);
        }
        return c;
    }


    public sealed class StreamingComputer
    {
        private readonly long _totalLength;
        private readonly List<byte> _tail = new(12);
        private uint _a;
        private uint _b;
        private uint _c;
        private long _processed;
        private long _received;
        private bool _finished;

        public StreamingComputer(long totalLength, uint initval = 0)
        {
            if (totalLength < 0 || totalLength > uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(totalLength), "Hash length must fit the PAZ/PAMT UInt32 field.");
            _totalLength = totalLength;
            _a = 0xDEADBEEF + (uint)totalLength + initval;
            _b = _a;
            _c = _a;
        }

        public void Update(ReadOnlySpan<byte> data)
        {
            if (_finished) throw new InvalidOperationException("Cannot update a finished PAZ hash.");
            if (data.Length == 0) return;
            if (_received + data.Length > _totalLength)
                throw new InvalidDataException("PAZ hash received more bytes than the planned PAZ part length.");

            _received += data.Length;
            int offset = 0;

            if (_tail.Count > 0)
            {
                while (_tail.Count < 12 && offset < data.Length)
                    _tail.Add(data[offset++]);

                if (_tail.Count == 12 && (_processed + 12) < _totalLength)
                {
                    Span<byte> block = stackalloc byte[12];
                    for (int i = 0; i < 12; i++) block[i] = _tail[i];
                    ProcessBlock(block);
                    _processed += 12;
                    _tail.Clear();
                }
            }

            while (offset + 12 <= data.Length && (_processed + 12) < _totalLength)
            {
                ProcessBlock(data.Slice(offset, 12));
                offset += 12;
                _processed += 12;
            }

            for (int i = offset; i < data.Length; i++)
                _tail.Add(data[i]);
        }

        public uint Finish()
        {
            if (_finished) return _c;
            if (_received != _totalLength)
                throw new InvalidDataException($"PAZ hash finished with {_received} bytes but expected {_totalLength} bytes.");
            if (_tail.Count > 12)
                throw new InvalidDataException("PAZ hash tail exceeded 12 bytes.");

            int length = _tail.Count;
            if (length > 0)
            {
                Span<byte> t = stackalloc byte[12];
                for (int i = 0; i < length; i++) t[i] = _tail[i];
                if (length >= 1) _a += t[0];
                if (length >= 2) _a += (uint)t[1] << 8;
                if (length >= 3) _a += (uint)t[2] << 16;
                if (length >= 4) _a += (uint)t[3] << 24;
                if (length >= 5) _b += t[4];
                if (length >= 6) _b += (uint)t[5] << 8;
                if (length >= 7) _b += (uint)t[6] << 16;
                if (length >= 8) _b += (uint)t[7] << 24;
                if (length >= 9) _c += t[8];
                if (length >= 10) _c += (uint)t[9] << 8;
                if (length >= 11) _c += (uint)t[10] << 16;
                if (length >= 12) _c += (uint)t[11] << 24;
                Final(ref _a, ref _b, ref _c);
            }
            _finished = true;
            return _c;
        }

        private void ProcessBlock(ReadOnlySpan<byte> block)
        {
            _a += BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(0, 4));
            _b += BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(4, 4));
            _c += BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(8, 4));
            Mix(ref _a, ref _b, ref _c);
        }
    }

    public static uint ComputeFile(string path, uint initval = 0, Action<long, long>? progress = null)
    {
        // Streaming parity with the Python implementation: process all full
        // 12-byte blocks except the final 1..12 byte tail.
        var info = new FileInfo(path);
        long total = info.Length;
        uint a = 0xDEADBEEF + (uint)total + initval;
        uint b = a;
        uint c = a;
        var tail = new List<byte>(24);
        long processed = 0;
        byte[] buffer = new byte[16 * 1024 * 1024];
        using var fs = File.OpenRead(path);
        while (true)
        {
            int read = fs.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            if (tail.Count > 0)
            {
                var merged = new byte[tail.Count + read];
                tail.CopyTo(merged, 0);
                Buffer.BlockCopy(buffer, 0, merged, tail.Count, read);
                ProcessStreamChunk(merged, merged.Length, total, ref processed, ref a, ref b, ref c, tail);
            }
            else
            {
                ProcessStreamChunk(buffer, read, total, ref processed, ref a, ref b, ref c, tail);
            }
            progress?.Invoke(processed, total);
        }
        int length = tail.Count;
        if (length > 0)
        {
            Span<byte> t = stackalloc byte[12];
            for (int i = 0; i < length; i++) t[i] = tail[i];
            if (length >= 1) a += t[0];
            if (length >= 2) a += (uint)t[1] << 8;
            if (length >= 3) a += (uint)t[2] << 16;
            if (length >= 4) a += (uint)t[3] << 24;
            if (length >= 5) b += t[4];
            if (length >= 6) b += (uint)t[5] << 8;
            if (length >= 7) b += (uint)t[6] << 16;
            if (length >= 8) b += (uint)t[7] << 24;
            if (length >= 9) c += t[8];
            if (length >= 10) c += (uint)t[9] << 8;
            if (length >= 11) c += (uint)t[10] << 16;
            if (length >= 12) c += (uint)t[11] << 24;
            Final(ref a, ref b, ref c);
        }
        progress?.Invoke(total, total);
        return c;
    }

    private static void ProcessStreamChunk(byte[] data, int count, long totalLen, ref long processed, ref uint a, ref uint b, ref uint c, List<byte> tail)
    {
        tail.Clear();
        int offset = 0;
        while ((count - offset) >= 12 && (processed + 12) < totalLen)
        {
            a += BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            b += BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
            c += BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8, 4));
            Mix(ref a, ref b, ref c);
            offset += 12;
            processed += 12;
        }
        for (int i = offset; i < count; i++) tail.Add(data[i]);
    }

    public static uint ComputePamtHash(byte[] pamt) => pamt.Length >= 12 ? Compute(pamt.AsSpan(12), IntegritySeed) : 0;
    public static uint ComputePapgtHash(byte[] papgt) => papgt.Length >= 12 ? Compute(papgt.AsSpan(12), IntegritySeed) : 0;
}
