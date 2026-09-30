using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace X0.MultiPlay
{
    // AES-256-CBC, 키 없으면 평문. 서버(crypto.rs)랑 형식 같아야 함
    public class PacketCrypto
    {
        readonly byte[] _key;
        const int IvLength = 16;

        public bool IsEnabled => _key != null;

        public PacketCrypto(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                _key = null;
                return;
            }
            _key = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(key.Trim()));
        }

        public string Encrypt(string plainText)
        {
            if (_key == null) return plainText;
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.GenerateIV();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
            var result = new byte[aes.IV.Length + cipherBytes.Length];
            Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
            Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);
            return Convert.ToBase64String(result);
        }

        public string Decrypt(string cipherBase64)
        {
            if (_key == null) return cipherBase64;
            try
            {
                var allBytes = Convert.FromBase64String(cipherBase64);
                if (allBytes.Length < IvLength) return cipherBase64;
                using var aes = Aes.Create();
                aes.Key = _key;
                var iv = new byte[IvLength];
                Buffer.BlockCopy(allBytes, 0, iv, 0, IvLength);
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using var decryptor = aes.CreateDecryptor();
                var cipherBytes = new byte[allBytes.Length - IvLength];
                Buffer.BlockCopy(allBytes, IvLength, cipherBytes, 0, cipherBytes.Length);
                var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return cipherBase64;
            }
        }

        public string EncodeForSend(string json) => IsEnabled ? Encrypt(json) : json;

        public string DecodeFromReceive(string line)
        {
            if (!IsEnabled || string.IsNullOrEmpty(line)) return line;
            if (!LooksLikeBase64(line)) return line;
            return Decrypt(line);
        }

        static bool LooksLikeBase64(string s) =>
            s.Length >= 20 && s.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=');
    }
}
