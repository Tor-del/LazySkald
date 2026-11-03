using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ScreenReader.App;

public static class SecretProtection
{
    public static string Protect(string value)=>string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value),true));
    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        byte[] decoded=Transform(Convert.FromBase64String(value),false);
        try { return Encoding.UTF8.GetString(decoded); }
        finally { CryptographicOperations.ZeroMemory(decoded); }
    }
    private static byte[] Transform(byte[] bytes,bool protect)
    {
        var input=new Blob { Length=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length) };
        Blob output=default;
        try
        {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            bool ok=protect ? CryptProtectData(ref input,null,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output)
                : CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(),UiText.T("error.secret"));
            byte[] result=new byte[output.Length]; Marshal.Copy(output.Data,result,0,result.Length); return result;
        }
        finally
        {
            for (int i=0; i<input.Length; i++) Marshal.WriteByte(input.Data,i,0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data!=IntPtr.Zero) { for (int i=0; i<output.Length; i++) Marshal.WriteByte(output.Data,i,0); LocalFree(output.Data); }
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
