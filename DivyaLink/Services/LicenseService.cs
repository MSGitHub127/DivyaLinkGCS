using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using System.Linq;

namespace DivyaLink.Services
{
    public class LicenseService
    {
        private const string PublicKeyXml = "<RSAKeyValue><Modulus>1lBEZ4TONKtUWTBUtWs2fDSeSu5fhu2HhxDWwyo+xxq5xweRYVi0UOELcItq7yHy4sbHfhHSBzAJ/mLZtLjEP6OYOM5/pkTnT7sJH88SRb7nlmf/liJsZpWTgan/A1bKpGg9kQmyJ7+L9M0tybD8WdbphO5uEd2+T1164horQqTtGp5sP45h6zVRtZF77jWF8KytffzKfCL4yDIMAhTbFvbG25r4OXtzWhyfXO4cFirWXEfqL1JOydh4SeAX42ZWd3F/SOxV65sK+bhccaBSmShCKKAF53uj/3cDUQZkk0vI5KIIwkHNB+SOguiCeM3sdRr2yqAHa6IOLKDn5GAp4Q==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        private readonly string _licenseFilePath;
        private readonly string _trialFilePath;
        
        public string HardwareId { get; }
        public bool IsActive { get; private set; }
        public string LicenseTier { get; private set; } = "NO LICENSE";
        public string RemainingDaysText { get; private set; } = "EXPIRED";
        public DateTime? ExpiryDate { get; private set; }

        public LicenseService()
        {
            // Store license key in application root folder
            _licenseFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "license.key");
            _trialFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DivyaLink",
                "trial.dat"
            );
            
            HardwareId = GenerateHardwareId();
            LoadAndValidateLicense();
        }

        public void LoadAndValidateLicense()
        {
            bool hasValidLicense = false;
            if (File.Exists(_licenseFilePath))
            {
                try
                {
                    string keyContent = File.ReadAllText(_licenseFilePath).Trim();
                    if (ValidateLicense(keyContent, out string tier, out string remainingDaysText, out DateTime? expiryDate))
                    {
                        IsActive = true;
                        LicenseTier = tier;
                        RemainingDaysText = remainingDaysText;
                        ExpiryDate = expiryDate;
                        hasValidLicense = true;
                    }
                }
                catch
                {
                    hasValidLicense = false;
                }
            }

            if (!hasValidLicense)
            {
                ValidateOrCreateTrial();
            }
        }

        private void ValidateOrCreateTrial()
        {
            try
            {
                string? directory = Path.GetDirectoryName(_trialFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                DateTime trialStartDate;
                if (!File.Exists(_trialFilePath))
                {
                    trialStartDate = DateTime.UtcNow;
                    string encryptedData = EncryptTrialDate(trialStartDate);
                    File.WriteAllText(_trialFilePath, encryptedData);
                }
                else
                {
                    string encryptedData = File.ReadAllText(_trialFilePath).Trim();
                    if (!DecryptTrialDate(encryptedData, out trialStartDate))
                    {
                        IsActive = false;
                        LicenseTier = "TRIAL EXPIRED";
                        RemainingDaysText = "EXPIRED";
                        ExpiryDate = null;
                        return;
                    }
                }

                DateTime now = DateTime.UtcNow;
                TimeSpan diff = now - trialStartDate;

                if (diff.TotalDays < 0 || diff.TotalDays > 25)
                {
                    IsActive = false;
                    LicenseTier = "TRIAL EXPIRED";
                    RemainingDaysText = "EXPIRED";
                    ExpiryDate = null;
                }
                else
                {
                    IsActive = true;
                    LicenseTier = "FREE TRIAL";
                    int remainingDays = 25 - (int)diff.TotalDays;
                    RemainingDaysText = $"{remainingDays} DAYS TRIAL";
                    ExpiryDate = trialStartDate.AddDays(25);
                }
            }
            catch
            {
                IsActive = false;
                LicenseTier = "ERROR";
                RemainingDaysText = "EXPIRED";
                ExpiryDate = null;
            }
        }

        private byte[] GetCryptoKey()
        {
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(Encoding.UTF8.GetBytes(HardwareId));
            }
        }

        private byte[] GetCryptoIV()
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes("divyalink-salt"));
                byte[] iv = new byte[16];
                Array.Copy(hash, iv, 16);
                return iv;
            }
        }

        private string EncryptTrialDate(DateTime date)
        {
            string plaintext = date.ToString("o");
            byte[] key = GetCryptoKey();
            byte[] iv = GetCryptoIV();

            using (Aes aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;

                using (MemoryStream ms = new MemoryStream())
                {
                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
                        cs.Write(plaintextBytes, 0, plaintextBytes.Length);
                        cs.FlushFinalBlock();
                    }
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        private bool DecryptTrialDate(string encryptedText, out DateTime date)
        {
            date = DateTime.MinValue;
            try
            {
                byte[] ciphertext = Convert.FromBase64String(encryptedText);
                byte[] key = GetCryptoKey();
                byte[] iv = GetCryptoIV();

                using (Aes aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.IV = iv;

                    using (MemoryStream ms = new MemoryStream(ciphertext))
                    {
                        using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read))
                        {
                            using (StreamReader sr = new StreamReader(cs))
                            {
                                string plaintext = sr.ReadToEnd();
                                return DateTime.TryParse(plaintext, null, System.Globalization.DateTimeStyles.RoundtripKind, out date);
                            }
                        }
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        public bool ValidateLicense(string licenseKey, out string tier, out string remainingDaysText, out DateTime? expiryDate)
        {
            tier = "INVALID";
            remainingDaysText = "EXPIRED";
            expiryDate = null;

            if (string.IsNullOrWhiteSpace(licenseKey))
                return false;

            string[] parts = licenseKey.Split('.');
            if (parts.Length != 2)
                return false;

            try
            {
                byte[] payloadBytes = Convert.FromBase64String(parts[0]);
                byte[] signatureBytes = Convert.FromBase64String(parts[1]);

                // 1. Verify RSA Signature
                using (var rsa = RSA.Create())
                {
                    rsa.FromXmlString(PublicKeyXml);
                    bool isSignatureValid = rsa.VerifyData(
                        payloadBytes,
                        signatureBytes,
                        HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1);

                    if (!isSignatureValid)
                        return false;
                }

                // 2. Parse Payload
                string payload = Encoding.UTF8.GetString(payloadBytes);
                string[] payloadParts = payload.Split('|');
                if (payloadParts.Length != 2)
                    return false;

                string licenseHwId = payloadParts[0];
                string expiryString = payloadParts[1];

                // 3. Match Hardware ID
                if (!string.Equals(HardwareId, licenseHwId, StringComparison.OrdinalIgnoreCase))
                    return false;

                // 4. Validate Expiry
                if (!DateTime.TryParse(expiryString, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsedExpiry))
                    return false;

                expiryDate = parsedExpiry.ToLocalTime();
                DateTime now = DateTime.UtcNow;

                if (parsedExpiry <= now)
                {
                    tier = "EXPIRED";
                    remainingDaysText = "EXPIRED";
                    return false;
                }

                // 5. Calculate remaining days & map tier
                TimeSpan diff = parsedExpiry - now;
                
                // If it is set to Dec 31, 2099 (which the keygen uses for lifetime)
                if (parsedExpiry.Year >= 2099)
                {
                    tier = "LIFETIME PRO";
                    remainingDaysText = "LIFETIME";
                }
                else
                {
                    tier = "PRO";
                    int days = (int)Math.Ceiling(diff.TotalDays);
                    remainingDaysText = $"{days} DAYS";
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public void SaveLicenseKey(string licenseKey)
        {
            File.WriteAllText(_licenseFilePath, licenseKey.Trim());
            LoadAndValidateLicense();
        }

        private string GenerateHardwareId()
        {
            try
            {
                string rawId = "";

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (File.Exists("/etc/machine-id"))
                        rawId = File.ReadAllText("/etc/machine-id").Trim();
                    else if (File.Exists("/var/lib/dbus/machine-id"))
                        rawId = File.ReadAllText("/var/lib/dbus/machine-id").Trim();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Reg key fallback for local testing on Windows
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                    var val = key?.GetValue("MachineGuid");
                    if (val != null)
                        rawId = val.ToString() ?? "";
                }

                if (string.IsNullOrWhiteSpace(rawId))
                {
                    // Fallback to active network interface MAC address
                    var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && 
                                             n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);
                    
                    if (nic != null)
                        rawId = nic.GetPhysicalAddress().ToString();
                    else
                        rawId = "divyalink-default-hw-id";
                }

                return HashId(rawId);
            }
            catch
            {
                return "divyalink-error-hw-id";
            }
        }

        private string HashId(string input)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                // Convert to uppercase hex and take first 16 chars for a clean HWID format
                return Convert.ToHexString(hashBytes).Substring(0, 16).ToUpper();
            }
        }
    }
}
