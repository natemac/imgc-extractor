using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Numerics;

public sealed class GameBinResult
{
    public byte[] Bytes = Array.Empty<byte>();
    public string OutputSha256 = string.Empty;
    public int Revision;
    public string InputSha256 = string.Empty;
    public string Version = string.Empty;
}

public static class GameBinDecoder
{
    const int ChunkSize = 1048576;
    static readonly int[] SwapOffsets = { 0, 1, 2, 3, 100, 200, 300, 400, 500, 600, 700, 800 };

    static byte[] Slice(byte[] source, int start, int count)
    {
        if (start < 0 || count < 0 || start > source.Length - count)
            throw new InvalidDataException("Truncated container.");
        byte[] result = new byte[count];
        Buffer.BlockCopy(source, start, result, 0, count);
        return result;
    }
    static void Swap(byte[] data, int a, int b)
    { byte temp = data[a]; data[a] = data[b]; data[b] = temp; }
    static void CString(Stream target, byte[] data, int start, int limit)
    {
        int end = start;
        while (end < limit && data[end] != 0) end++;
        if (end == limit) throw new InvalidDataException("Invalid container text field.");
        target.Write(data, start, end - start);
    }
    static void NonzeroByte(Stream target, byte value) { target.WriteByte(value == 0 ? (byte)255 : value); }
    static void Text(Stream target, string value)
    { byte[] bytes = Encoding.ASCII.GetBytes(value); target.Write(bytes, 0, bytes.Length); }

    static byte[] PayloadKey(byte[] input)
    {
        byte[] digest;
        using (MemoryStream seed = new MemoryStream())
        {
            NonzeroByte(seed, input[84]);
            CString(seed, input, 4, 20);
            NonzeroByte(seed, input[88]);
            CString(seed, input, 20, 84);
            NonzeroByte(seed, input[96]);
            Text(seed, " Federal Law protects this file ");
            NonzeroByte(seed, input[92]);
            digest = GameBinCrypto.Sha1(seed.ToArray());
        }
        byte[] blob = GameBinCrypto.Rc4(Slice(input, 164, 48), digest);
        for (int j = 0; j < 8; j++) Swap(blob, 1 + 4 * j, 32 + j);
        for (int j = 0; j < 8; j++) Swap(blob, 31 - 4 * j, 40 + j);
        return Slice(blob, 0, 32);
    }

    static byte[] Metadata(byte[] input)
    {
        using (MemoryStream seed = new MemoryStream())
        {
            NonzeroByte(seed, input[84]);
            CString(seed, input, 4, 20);
            NonzeroByte(seed, input[88]);
            NonzeroByte(seed, input[92]);
            NonzeroByte(seed, input[96]);
            NonzeroByte(seed, input[2]);
            CString(seed, input, 20, 84);
            Text(seed, " this Key is property of IT ");
            return GameBinCrypto.Rc4(Slice(input, 212, 368), GameBinCrypto.Sha1(seed.ToArray()));
        }
    }
    static bool Equal(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int difference = 0;
        for (int n = 0; n < a.Length; n++) difference |= a[n] ^ b[n];
        return difference == 0;
    }
    static BigInteger Integer(byte[] data, int offset, int length)
    {
        byte[] bytes = new byte[length + 1];
        for (int n = 0; n < length; n++) bytes[n] = data[offset + length - 1 - n];
        return new BigInteger(bytes);
    }
    static BigInteger Inverse(BigInteger value, BigInteger modulus)
    {
        BigInteger t = 0, nextT = 1, r = modulus, nextR = value;
        while (nextR != 0)
        {
            BigInteger quotient = r / nextR;
            BigInteger temp = t - quotient * nextT; t = nextT; nextT = temp;
            temp = r - quotient * nextR; r = nextR; nextR = temp;
        }
        if (r != 1) throw new InvalidDataException("Invalid container signature inverse.");
        return t < 0 ? t + modulus : t;
    }
    static void VerifyIntegrity(byte[] metadata, byte[] output)
    {
        byte[] digest = GameBinCrypto.Sha1(output);
        if (!Equal(digest, Slice(metadata, 212, 20)))
            throw new InvalidDataException("Payload checksum failed; the source is damaged or unsupported.");
        BigInteger y = Integer(metadata, 0, 96), r = Integer(metadata, 96, 20);
        BigInteger p = Integer(metadata, 116, 96), g = Integer(metadata, 232, 96);
        BigInteger q = Integer(metadata, 328, 20), s = Integer(metadata, 348, 20);
        if (p <= 2 || q <= 2 || q >= p || p.IsEven || q.IsEven ||
            g <= 1 || g >= p || y <= 0 || y >= p || r <= 0 || r >= q || s <= 0 || s >= q)
            throw new InvalidDataException("Invalid container signature parameters.");
        BigInteger w = Inverse(s, q);
        BigInteger u1 = Integer(digest, 0, 20) * w % q, u2 = r * w % q;
        BigInteger verified = (BigInteger.ModPow(g, u1, p) * BigInteger.ModPow(y, u2, p) % p) % q;
        if (verified != r) throw new InvalidDataException("Container signature verification failed.");
    }
    static string Sha256(byte[] data)
    {
        using (SHA256 hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
    }
    static void Set32(byte[] data, int offset, uint value)
    { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, data, offset, 4); }
    static void Adjust16(byte[] data, int offset, int delta)
    { Buffer.BlockCopy(BitConverter.GetBytes(unchecked((ushort)(BitConverter.ToUInt16(data, offset) + delta))), 0, data, offset, 2); }
    static void VerifyPrefix(byte[] prefix, byte[] output)
    {
        byte[] transformed = Slice(output, 0, 52);
        Set32(transformed, 0, 0x464c457f);
        Set32(transformed, 4, 0x00010101);
        Set32(transformed, 24, unchecked(BitConverter.ToUInt32(transformed, 24) + 128));
        Set32(transformed, 28, unchecked(BitConverter.ToUInt32(transformed, 28) + 64));
        Set32(transformed, 32, unchecked(BitConverter.ToUInt32(transformed, 32) - 96));
        Adjust16(transformed, 42, 16); Adjust16(transformed, 44, -1);
        Adjust16(transformed, 48, 1); Adjust16(transformed, 50, 32);
        if (!Equal(Slice(prefix, 0, 52), transformed))
            throw new InvalidDataException("Container executable header verification failed.");
    }

    public static GameBinResult Decode(byte[] input)
    {
        if (input == null || input.Length < 600 || input.Length > 134217728)
            throw new InvalidDataException("Invalid executable container size.");
        if (BitConverter.ToUInt16(input, 0) != 100)
            throw new InvalidDataException("This is not a supported encrypted game.bin container.");
        int revision = BitConverter.ToUInt16(input, 2);
        if (revision != 2 && revision != 3)
            throw new InvalidDataException("Unsupported container revision. Only revisions 2 and 3 are verified; revision 1 requires an older loader.");
        if (BitConverter.ToUInt32(input, 84) != 14)
            throw new InvalidDataException("This tool supports supported product 14 only.");
        int bodyOffset = revision == 3 ? 584 : 580;
        if ((input.Length - bodyOffset) % 16 != 0)
            throw new InvalidDataException("The encrypted payload is truncated or has trailing data.");
        byte[] key = PayloadKey(input);
        byte[] prefix = GameBinCrypto.Aes256EcbDecrypt(Slice(input, 100, 64), key);
        if (BitConverter.ToUInt32(prefix, 0) != 0x464c457f || BitConverter.ToUInt32(prefix, 4) != 0x00010101)
            throw new InvalidDataException("The decoded container marker is invalid.");
        byte[] output = new byte[input.Length - bodyOffset];
        for (int offset = 0; offset < output.Length; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, output.Length - offset);
            byte[] chunk = Slice(input, bodyOffset + offset, count);
            if (count == ChunkSize)
                foreach (int index in SwapOffsets) Swap(chunk, index, count - 1 - index);
            byte[] decoded = GameBinCrypto.Aes256EcbDecrypt(chunk, key);
            Buffer.BlockCopy(decoded, 0, output, offset, count);
        }
        Array.Clear(key, 0, key.Length);
        if (output.Length < 52 || BitConverter.ToUInt32(output, 0) != 0x464c457f ||
            output[4] != 1 || output[5] != 1 || BitConverter.ToUInt16(output, 18) != 3)
            throw new InvalidDataException("Decoded file is not a supported ELF32 i386 executable.");
        VerifyPrefix(prefix, output);
        VerifyIntegrity(Metadata(input), output);
        return new GameBinResult {
            Bytes = output, OutputSha256 = Sha256(output), InputSha256 = Sha256(input), Revision = revision,
            Version = BitConverter.ToUInt32(input, 88) + "." + BitConverter.ToUInt32(input, 92).ToString("D2") + "." + BitConverter.ToUInt32(input, 96).ToString("D2")
        };
    }
}
