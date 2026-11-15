using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// age v1（X25519）客户端加密 —— 只加密，绝不解密。
/// 完全复刻 age-encryption.org/v1 规范，产出标准 ASCII armor 密文，可被标准 age 工具用私钥解密。
/// <para>
/// 私钥仅开发者本地持有，绝不进入任何客户端代码或版本库；本类只持有接收方公钥、只做加密。
/// </para>
/// </summary>
public static class AgeEncryption
{
    /// <summary>「提交软件」联系方式字段的接收方公钥（age1...）。私钥绝不在客户端。</summary>
    public const string RecipientPublicKey = "age1l9axcy0sxu6eeanapg0maughsv380fh8nhd4unf9p8d7x20rjqfsgz4dxa";

    private const string V1 = "age-encryption.org/v1";
    private const string X25519Label = "age-encryption.org/v1/X25519";

    /// <summary>用内置接收方公钥加密 UTF-8 明文，返回 ASCII armor 密文。</summary>
    public static string EncryptToArmor(string plaintext)
        => EncryptToArmor(RecipientPublicKey, plaintext);

    /// <summary>用 age 公钥（age1...）加密 UTF-8 明文，返回 ASCII armor 密文。</summary>
    public static string EncryptToArmor(string recipient, string plaintext)
    {
        byte[] recipientPub = Bech32Decode(recipient);
        byte[] binary = Encrypt(recipientPub, Encoding.UTF8.GetBytes(plaintext));
        return ArmorEncode(binary);
    }

    private static byte[] Encrypt(byte[] recipientPub, byte[] plaintext)
    {
        // 1. file key（16 字节随机）
        byte[] fileKey = RandomNumberGenerator.GetBytes(16);

        // 2. X25519 stanza：包装 file key
        byte[] ephemeralSecret = RandomNumberGenerator.GetBytes(32);
        byte[] ephemeralShare = X25519(ephemeralSecret, Basepoint);   // 32 字节临时公钥
        byte[] sharedSecret = X25519(ephemeralSecret, recipientPub);  // 32 字节

        byte[] salt = new byte[64];
        ephemeralShare.CopyTo(salt, 0);
        recipientPub.CopyTo(salt, 32);
        byte[] wrapKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32,
            salt, Encoding.ASCII.GetBytes(X25519Label));

        byte[] body = ChaChaEncrypt(wrapKey, fileKey);              // 32 字节（密文 16 + tag 16）

        // 3. header（无 MAC 部分）
        string headerNoMac = $"{V1}\n-> X25519 {B64NoPad(ephemeralShare)}\n{B64NoPad(body)}\n---";
        byte[] hmacKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, fileKey, 32,
            null, Encoding.ASCII.GetBytes("header"));
        byte[] mac = new HMACSHA256(hmacKey).ComputeHash(Encoding.ASCII.GetBytes(headerNoMac)); // 32 字节

        byte[] header = Encoding.ASCII.GetBytes(headerNoMac + " " + B64NoPad(mac) + "\n");

        // 4. payload
        byte[] payloadNonce = RandomNumberGenerator.GetBytes(16);
        byte[] streamKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, fileKey, 32,
            payloadNonce, Encoding.ASCII.GetBytes("payload"));
        byte[] payload = ChaChaEncryptStream(streamKey, plaintext);

        // 5. 拼接：header + payloadNonce + payload
        var output = new byte[header.Length + payloadNonce.Length + payload.Length];
        header.CopyTo(output, 0);
        payloadNonce.CopyTo(output, header.Length);
        payload.CopyTo(output, header.Length + payloadNonce.Length);
        return output;
    }

    /// <summary>ChaCha20-Poly1305 加密（nonce 12 字节全 0，无 AAD），返回 密文||tag。</summary>
    private static byte[] ChaChaEncrypt(byte[] key, byte[] plaintext)
    {
        using var cipher = new ChaCha20Poly1305(key);
        byte[] nonce = new byte[12];
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        cipher.Encrypt(nonce, plaintext, ciphertext, tag);
        var result = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(result, 0);
        tag.CopyTo(result, ciphertext.Length);
        return result;
    }

    /// <summary>payload 流式加密：64 KiB 分块，nonce = 11 字节大端计数器 + 1 字节末尾标志。</summary>
    private static byte[] ChaChaEncryptStream(byte[] key, byte[] plaintext)
    {
        const int chunkSize = 64 * 1024;
        using var output = new MemoryStream();
        byte[] nonce = new byte[12];
        int offset = 0;
        while (offset < plaintext.Length)
        {
            int n = Math.Min(chunkSize, plaintext.Length - offset);
            bool isLast = offset + n >= plaintext.Length;
            nonce[11] = isLast ? (byte)1 : (byte)0;

            using var cipher = new ChaCha20Poly1305(key);
            byte[] ciphertext = new byte[n];
            byte[] tag = new byte[16];
            cipher.Encrypt(nonce, plaintext.AsSpan(offset, n), ciphertext, tag);
            output.Write(ciphertext);
            output.Write(tag);
            offset += n;

            // 大端计数器 +1（索引 10 是最低位）
            for (int i = 10; i >= 0; i--)
                if (++nonce[i] != 0) break;
        }
        return output.ToArray();
    }

    // ── X25519（RFC 7748，用 BigInteger 实现；只用于"公钥加密"，无常数时间要求）──
    private static readonly byte[] Basepoint = { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                                                 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    private static byte[] X25519(byte[] scalar, byte[] uCoord)
    {
        BigInteger p = (BigInteger.One << 255) - 19;
        BigInteger a24 = 121665;

        // clamp 标量
        byte[] k = (byte[])scalar.Clone();
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;

        BigInteger x1 = Mod(LittleEndian(uCoord), p);
        BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
        int swap = 0;
        for (int t = 254; t >= 0; t--)
        {
            int kt = (k[t >> 3] >> (t & 7)) & 1;
            swap ^= kt;
            if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
            swap = kt;

            BigInteger A = Mod(x2 + z2, p);
            BigInteger AA = Mod(A * A, p);
            BigInteger B = Mod(x2 - z2, p);
            BigInteger BB = Mod(B * B, p);
            BigInteger E = Mod(AA - BB, p);
            BigInteger C = Mod(x3 + z3, p);
            BigInteger D = Mod(x3 - z3, p);
            BigInteger DA = Mod(D * A, p);
            BigInteger CB = Mod(C * B, p);
            x3 = Mod(DA + CB, p); x3 = Mod(x3 * x3, p);
            z3 = Mod(DA - CB, p); z3 = Mod(x1 * Mod(z3 * z3, p), p);
            x2 = Mod(AA * BB, p);
            z2 = Mod(E * Mod(AA + Mod(a24 * E, p), p), p);
        }
        if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }

        BigInteger result = Mod(x2 * BigInteger.ModPow(z2, p - 2, p), p);
        return LittleEndian(result, 32);
    }

    private static BigInteger Mod(BigInteger a, BigInteger p)
    {
        a %= p;
        return a < 0 ? a + p : a;
    }

    private static BigInteger LittleEndian(byte[] bytes)
    {
        // 小端字节序 → 无符号大整数：反转后按无符号大端读
        var rev = (byte[])bytes.Clone();
        Array.Reverse(rev);
        return new BigInteger(rev, isUnsigned: true, isBigEndian: true);
    }

    private static byte[] LittleEndian(BigInteger value, int length)
    {
        byte[] be = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var result = new byte[length];
        for (int i = 0; i < be.Length && i < length; i++)
            result[i] = be[be.Length - 1 - i];
        return result;
    }

    private static string B64NoPad(byte[] data) => Convert.ToBase64String(data).TrimEnd('=');

    // ── armor（PEM 风格：每 48 字节 raw → 64 字符标准 base64 一行）──
    private static string ArmorEncode(byte[] file)
    {
        var sb = new StringBuilder();
        sb.Append("-----BEGIN AGE ENCRYPTED FILE-----\n");
        for (int i = 0; i < file.Length; i += 48)
        {
            int n = Math.Min(48, file.Length - i);
            sb.Append(Convert.ToBase64String(file, i, n)).Append('\n');
        }
        sb.Append("-----END AGE ENCRYPTED FILE-----\n");
        return sb.ToString();
    }

    // ── bech32 解码（age1... → 32 字节公钥）──
    private static readonly char[] Bech32Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l".ToCharArray();
    private static readonly uint[] Bech32Gen = { 0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3 };

    private static byte[] Bech32Decode(string str)
    {
        if (str.Length < 8 || str.Length > 90) throw new Exception("invalid bech32 length");
        bool hasLower = str.Any(char.IsLower), hasUpper = str.Any(char.IsUpper);
        if (hasLower && hasUpper) throw new Exception("mixed case bech32");
        str = str.ToLowerInvariant();

        int pos = str.LastIndexOf('1');
        if (pos < 1 || pos + 7 > str.Length) throw new Exception("invalid separator");
        string hrp = str[..pos];
        string data = str[(pos + 1)..];

        var values = new List<byte>(HrpExpand(hrp));
        foreach (char c in data)
        {
            int idx = Array.IndexOf(Bech32Charset, c);
            if (idx < 0) throw new Exception("invalid bech32 char");
            values.Add((byte)idx);
        }
        if (Bech32Polymod(values.ToArray()) != 1) throw new Exception("bech32 checksum mismatch");

        var dataWords = new byte[data.Length - 6];
        for (int i = 0; i < dataWords.Length; i++)
            dataWords[i] = (byte)Array.IndexOf(Bech32Charset, data[i]);

        return ConvertBits(dataWords, 5, 8, pad: false);
    }

    private static byte[] HrpExpand(string hrp)
    {
        var result = new List<byte>();
        foreach (char c in hrp) result.Add((byte)(c >> 5));
        result.Add(0);
        foreach (char c in hrp) result.Add((byte)(c & 31));
        return result.ToArray();
    }

    private static uint Bech32Polymod(byte[] values)
    {
        uint chk = 1;
        foreach (byte v in values)
        {
            uint b = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ v;
            for (int i = 0; i < 5; i++)
                if (((b >> i) & 1) == 1) chk ^= Bech32Gen[i];
        }
        return chk;
    }

    private static byte[] ConvertBits(byte[] data, int fromBits, int toBits, bool pad)
    {
        int acc = 0, bits = 0;
        var result = new List<byte>();
        int maxv = (1 << toBits) - 1;
        foreach (byte value in data)
        {
            acc = (acc << fromBits) | value;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((byte)((acc >> bits) & maxv));
            }
        }
        if (pad)
        {
            if (bits > 0) result.Add((byte)((acc << (toBits - bits)) & maxv));
        }
        else if (bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0)
        {
            throw new Exception("invalid padding");
        }
        return result.ToArray();
    }
}
