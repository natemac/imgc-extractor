using System;
using System.Security.Cryptography;
using System.IO;

public static class GameBinCrypto
{
    public static string FileSha256(string path)
    {
        using (FileStream stream = File.OpenRead(path))
        using (SHA256 hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    public static byte[] Sha1(byte[] data)
    {
        using (SHA1 hash = SHA1.Create()) return hash.ComputeHash(data);
    }

    public static byte[] Rc4(byte[] data, byte[] key)
    {
        if (key == null || key.Length == 0) throw new ArgumentException("Empty RC4 key.");
        byte[] state = new byte[256];
        for (int n = 0; n < 256; n++) state[n] = (byte)n;
        int j = 0;
        for (int n = 0; n < 256; n++)
        {
            j = (j + state[n] + key[n % key.Length]) & 255;
            byte temp = state[n]; state[n] = state[j]; state[j] = temp;
        }
        byte[] output = new byte[data.Length];
        int i = 0; j = 0;
        for (int n = 0; n < data.Length; n++)
        {
            i = (i + 1) & 255; j = (j + state[i]) & 255;
            byte temp = state[i]; state[i] = state[j]; state[j] = temp;
            output[n] = (byte)(data[n] ^ state[(state[i] + state[j]) & 255]);
        }
        Array.Clear(state, 0, state.Length);
        return output;
    }

    public static byte[] Aes256EcbDecrypt(byte[] data, byte[] key)
    {
        if (key == null || key.Length != 32) throw new ArgumentException("AES-256 requires 32 key bytes.");
        if (data.Length % 16 != 0) throw new ArgumentException("AES input must contain whole blocks.");
        using (Aes cipher = Aes.Create())
        {
            cipher.Mode = CipherMode.ECB;
            cipher.Padding = PaddingMode.None;
            cipher.Key = key;
            using (ICryptoTransform transform = cipher.CreateDecryptor())
                return transform.TransformFinalBlock(data, 0, data.Length);
        }
    }
}
