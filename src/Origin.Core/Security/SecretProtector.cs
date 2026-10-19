using System;
using System.Security.Cryptography;
using System.Text;
using Origin.Core.Diagnostics;

namespace Origin.Core.Security
{
    /// <summary>
    /// Encrypts saved credentials with DPAPI so they can only be read by the same Windows user
    /// on the same PC.
    /// </summary>
    public static class SecretProtector
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Origin.HTPC.Credentials.v1");

        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
            {
                return null;
            }

            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>Returns the decrypted value, or null if it is missing or cannot be decrypted.</summary>
        public static string Unprotect(string protectedValue)
        {
            if (string.IsNullOrEmpty(protectedValue))
            {
                return null;
            }

            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (CryptographicException ex)
            {
                Log.Warn("A saved credential could not be decrypted. It may have been saved by another Windows user.", ex);
                return null;
            }
            catch (FormatException ex)
            {
                Log.Warn("A saved credential is not valid.", ex);
                return null;
            }
        }
    }
}
