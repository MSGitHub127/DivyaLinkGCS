// ═══════════════════════════════════════════════════════════════════════════
//  VayunoticsKeyGen — DivyaLink License Key Generator
//  Run this ONLY on the Vayunotics admin machine. Keep PrivateKeyXml secret.
//  Usage:
//    dotnet run
//    Enter hardware ID when prompted (from the client's license dialog)
//    Select license duration
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Security.Cryptography;
using System.Text;

class VayunoticsKeyGen
{
    // ── KEEP THIS PRIVATE KEY SECRET ─────────────────────────────────────────
    private const string PrivateKeyXml = "<RSAKeyValue><Modulus>1lBEZ4TONKtUWTBUtWs2fDSeSu5fhu2HhxDWwyo+xxq5xweRYVi0UOELcItq7yHy4sbHfhHSBzAJ/mLZtLjEP6OYOM5/pkTnT7sJH88SRb7nlmf/liJsZpWTgan/A1bKpGg9kQmyJ7+L9M0tybD8WdbphO5uEd2+T1164horQqTtGp5sP45h6zVRtZF77jWF8KytffzKfCL4yDIMAhTbFvbG25r4OXtzWhyfXO4cFirWXEfqL1JOydh4SeAX42ZWd3F/SOxV65sK+bhccaBSmShCKKAF53uj/3cDUQZkk0vI5KIIwkHNB+SOguiCeM3sdRr2yqAHa6IOLKDn5GAp4Q==</Modulus><Exponent>AQAB</Exponent><P>807adcKh76Yso7nRwbtL8PrR/hNRihWovCVJ3YyuXx8uInnae3grj2jDtI8gLg5ZwgNjGIr3fUI89FMQeVRgcxljWvz5n+PMESGfOxsH5VVOo9pbJu1WgC9E6YJhBd2i0VD9fsJi254ZPJeMzqiYwKkEPsk0k8RkSZ4FK0U9nmc=</P><Q>4X44Yhw0rmrXPUxsfqTwuweL/GJSGgOlbuBCqKGQfUbHcMrB5eC2unPYEep6aUn1sgwHxoDo2y7HFvr02gMYP3Vea7GV2odOabiGjv3n3t1PRvJM0AvSmfdx9kX4zQ1BLCqOMfZNx0uA+5ZXVzZwGwydopR7T2wwXEOtw54jOHc=</Q><DP>vjYgPb3qYXM1JM5peJ5XUU6VCp/JuD0Ui/pO0+BelcjHhXZj4vDghR3vGeJm0vqvGykQuKgzsX4uLwgdMe1P9cucTA7HjENHTwFM/aU1OAnID/ruFfGoFVBe/HrMJQzPc/pwI0AOjwj7S85i61ENEllQE1GzQ+5eRNs/yUM5V/M=</DP><DQ>VbKNpSPJFbx/HsnLtfnjj4EXv4xyXXajSdcrHkGA00uyAnjcZgwYBhZ+uJhfe2JjYQ5XaiaV2K8XdPFdWvmwHnXxs4YjSJEByQYbBX8Tv0xmk7UEYlEL8f3rrsf6/Zsa+LkXn39XIfXdCECj4v5Kbs1Fn4NEtfONEZObF2wjQJM=</DQ><InverseQ>Q1RIxK0WLxbHPGdqMs4ZN8sKIJqq9LaoxKk3tMJUYU2HFNB9Ub/tIbXYhL6o0+FeZzEEr3/zthy5mO0lppSlZZ3LKfkN9Lqm0H0WohJOrH4ACVWVkMKCRVgC41s7jBF6CTKmyX9GnbFkMjxVrKJGc3f+5IN1dJO8l8wpL8OJUQA=</InverseQ><D>VhDwF56G7THs8tBtBFplDCZZd4AZTudvKPfDN6dshKsf9mT8plpoN57Y6D6lGBnLH/VyQhfH/+jz6nwqL+CPt3rsc8vCCbTcK/HR7TtMfEP5Xzauts1mOSBSl5z8c4vUX4gZSXjaWgobm5kWjUwNW8rqBt91xqkFd3v0EH8v5lf3N5UZZmoGNkbqmQ5U+WP5qTFjxrN+rewGIeg1QWhMGyt/W2EsmFAKEIPBFjIBOfmzlWijw31N0Eim59HoYsVxsM141kz6PbXjqzpCmWl2FonyIVYSMPVmtGwdGRMsf7QHRVGa02S/xGBYIVZaFcrFOg/XYeNiuXBd9NFy0Uq4PQ==</D></RSAKeyValue>";

    static void Main()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine("  VAYUNOTICS  —  DivyaLink Key Generator  v1.0");
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.ResetColor();

        Console.Write("\nEnter client Hardware ID: ");
        string hwId = Console.ReadLine()?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(hwId))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: Hardware ID cannot be empty.");
            Console.ResetColor();
            return;
        }

        string expiryInput = "";
        string selectedPlan = "";
        bool isConfirmed = false;

        // --- THE MENU & CONFIRMATION LOOP ---
        while (!isConfirmed)
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("═══════════════════════════════════════════════════");
            Console.WriteLine("  VAYUNOTICS  —  DivyaLink Key Generator  v1.0");
            Console.WriteLine("═══════════════════════════════════════════════════");
            Console.ResetColor();

            Console.WriteLine($"\nTarget Hardware ID: {hwId}");
            Console.WriteLine("\nSelect License Duration:");
            Console.WriteLine(" [1] 1 Month");
            Console.WriteLine(" [2] 3 Months");
            Console.WriteLine(" [3] 6 Months");
            Console.WriteLine(" [4] 1 Year");
            Console.WriteLine(" [5] Lifetime PRO");
            Console.WriteLine(" [6] Custom Date (yyyy-MM-dd)");
            Console.Write("\nEnter choice (1-6): ");

            string choice = Console.ReadLine()?.Trim() ?? "";

            // Get current time in India (UTC + 5:30)
            DateTime istNow = DateTime.UtcNow.AddHours(5).AddMinutes(30);

            switch (choice)
            {
                case "1":
                    expiryInput = istNow.AddMonths(1).ToString("yyyy-MM-dd");
                    selectedPlan = "1 Month";
                    break;
                case "2":
                    expiryInput = istNow.AddMonths(3).ToString("yyyy-MM-dd");
                    selectedPlan = "3 Months";
                    break;
                case "3":
                    expiryInput = istNow.AddMonths(6).ToString("yyyy-MM-dd");
                    selectedPlan = "6 Months";
                    break;
                case "4":
                    expiryInput = istNow.AddYears(1).ToString("yyyy-MM-dd");
                    selectedPlan = "1 Year";
                    break;
                case "5":
                    expiryInput = "lifetime";
                    selectedPlan = "Lifetime PRO";
                    break;
                case "6":
                    Console.Write("\nEnter Custom Expiration Date (yyyy-MM-dd): ");
                    expiryInput = Console.ReadLine()?.Trim() ?? "";
                    selectedPlan = "Custom Date";
                    break;
                default:
                    Console.WriteLine("\nInvalid choice. Press any key to try again...");
                    Console.ReadKey();
                    continue; // Restarts the menu
            }

            // The Confirmation Gate
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"\nYou selected: {selectedPlan} (Expires: {expiryInput}). Confirm generation? (y/n): ");
            Console.ResetColor();

            string confirm = Console.ReadLine()?.Trim().ToLower() ?? "";

            if (confirm == "y" || confirm == "yes")
            {
                isConfirmed = true;
                Console.WriteLine("\nGenerating Key...");
            }
        }

        string expiryDate;

        // --- THE TIMEZONE MATH ---
        if (expiryInput.ToLower() == "lifetime")
        {
            expiryDate = "2099-12-31T23:59:59.9999999Z";
            Console.WriteLine("→ Generating LIFETIME key");
        }
        else if (DateTime.TryParse(expiryInput, out DateTime parsed))
        {
            // 1. Force the date to the absolute end of the day (23:59:59)
            DateTime endOfDayIST = parsed.Date.AddDays(1).AddTicks(-1);

            // 2. Convert IST (UTC+5:30) back to UTC for the software payload
            // Subtracting 5.5 hours from midnight IST gives us 18:29:59 UTC
            DateTime finalExpiryUtc = endOfDayIST.AddHours(-5).AddMinutes(-30);

            // 3. Ensure C# treats it strictly as a UTC timestamp before stringifying
            finalExpiryUtc = DateTime.SpecifyKind(finalExpiryUtc, DateTimeKind.Utc);

            // 4. Format it for the cryptographic payload
            expiryDate = finalExpiryUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

            Console.WriteLine($"→ Generating key valid until {endOfDayIST:yyyy-MM-dd HH:mm:ss} IST");
            Console.WriteLine($"  (Internal System Payload: {finalExpiryUtc:yyyy-MM-dd HH:mm:ss} UTC)");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: Invalid date format. Use yyyy-MM-dd or 'lifetime'.");
            Console.ResetColor();
            return;
        }

        // --- CRYPTOGRAPHIC SIGNING ---
        string payload = $"{hwId}|{expiryDate}";

        try
        {
            using RSA rsa = RSA.Create();
            rsa.FromXmlString(PrivateKeyXml);

            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
            byte[] signature = rsa.SignData(
                payloadBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            string key = Convert.ToBase64String(payloadBytes) + "." +
                         Convert.ToBase64String(signature);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n═══════════════════════════════════════════════════");
            Console.WriteLine("  LICENSE KEY — SEND THIS TO THE CLIENT");
            Console.WriteLine("═══════════════════════════════════════════════════");
            Console.WriteLine(key);
            Console.WriteLine("═══════════════════════════════════════════════════");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\nERROR generating key: {ex.Message}");
            Console.WriteLine("Ensure the PrivateKeyXml is the full RSA key pair.");
            Console.ResetColor();
        }
    }
}