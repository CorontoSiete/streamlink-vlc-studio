using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreamlinkVlcStudio.Infrastructure.Settings;

internal static class SettingsSecretProtector
{
    private const int CurrentVersion = 1;
    private const string ProtectionKind = "DPAPI-CurrentUser";
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] OptionalEntropy =
        "StreamlinkVlcStudio.Settings.ProtectedSecrets.v1"u8.ToArray();

    internal static ProtectedSecretsEnvelope Protect(SettingsSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(secrets);
        try
        {
            var protectedBytes = Transform(plaintext, protect: true);
            try
            {
                return new ProtectedSecretsEnvelope
                {
                    Version = CurrentVersion,
                    Protection = ProtectionKind,
                    Ciphertext = Convert.ToBase64String(protectedBytes)
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static SettingsSecrets Unprotect(ProtectedSecretsEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Version != CurrentVersion ||
            !string.Equals(envelope.Protection, ProtectionKind, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(envelope.Ciphertext))
        {
            throw new CryptographicException("The protected settings envelope is unsupported or incomplete.");
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(envelope.Ciphertext);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("The protected settings envelope is not valid base64.", ex);
        }

        try
        {
            var plaintext = Transform(protectedBytes, protect: false);
            try
            {
                return JsonSerializer.Deserialize<SettingsSecrets>(plaintext)
                    ?? throw new CryptographicException("The decrypted settings secret payload was empty.");
            }
            catch (JsonException ex)
            {
                throw new CryptographicException("The decrypted settings secret payload was invalid.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI settings protection requires Windows.");
        }

        var inputBlob = default(DataBlob);
        var entropyBlob = default(DataBlob);
        var outputBlob = default(DataBlob);
        try
        {
            // Allocate under the cleanup scope so a failure allocating the second blob cannot
            // leak the first unmanaged buffer.
            inputBlob = AllocateBlob(input);
            entropyBlob = AllocateBlob(OptionalEntropy);
            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    "Streamlink VLC Studio settings",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);
            if (!succeeded)
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                throw new CryptographicException(
                    protect ? "DPAPI could not protect settings secrets." : "DPAPI could not decrypt settings secrets.",
                    error);
            }

            if (outputBlob.Length <= 0 || outputBlob.Data == IntPtr.Zero)
            {
                throw new CryptographicException("DPAPI returned an empty result.");
            }

            var output = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            FreeBlob(inputBlob);
            FreeBlob(entropyBlob);
            if (outputBlob.Data != IntPtr.Zero)
            {
                if (outputBlob.Length > 0)
                {
                    unsafe
                    {
                        CryptographicOperations.ZeroMemory(
                            new Span<byte>((void*)outputBlob.Data, outputBlob.Length));
                    }
                }

                _ = LocalFree(outputBlob.Data);
            }
        }
    }

    private static DataBlob AllocateBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob(bytes.Length, pointer);
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        unsafe
        {
            CryptographicOperations.ZeroMemory(
                new Span<byte>((void*)blob.Data, blob.Length));
        }

        Marshal.FreeHGlobal(blob.Data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        internal DataBlob(int length, IntPtr data)
        {
            Length = length;
            Data = data;
        }

        internal int Length;
        internal IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
