using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Invoklet.Runtime;

// Decrypts what Windows DPAPI encrypted for the current user (ProtectedData.Unprotect with
// DataProtectionScope.CurrentUser and no entropy), called directly so this library needs no
// System.Security.Cryptography.ProtectedData package.
internal static class Dpapi
{
    private const int UiForbidden = 0x1;

    public static byte[] Unprotect(byte[] encrypted)
    {
        var input = GCHandle.Alloc(encrypted, GCHandleType.Pinned);
        try
        {
            var inBlob = new DataBlob { Size = encrypted.Length, Data = input.AddrOfPinnedObject() };
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var outBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally { LocalFree(outBlob.Data); }
        }
        finally { input.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
