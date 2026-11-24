using System.Runtime.InteropServices;
using System.Text;

namespace KsaPort.Common;

// What macOS's sysctl says of this Mac (sysctlbyname): a number, a string, or a struct of numbers; null or "" when it
// doesn't say
public static class Sysctl
{
    public static long? Long(string name)
    {
        long value = 0;
        nint size = sizeof(long);
        return SysctlByName(name, ref value, ref size, 0, 0) == 0 ? value : null;
    }

    public static string Text(string name)
    {
        nint size = 0;
        if (SysctlByName(name, (byte[]?)null, ref size, 0, 0) != 0 || size <= 0)
            return "";
        var bytes = new byte[size];
        return SysctlByName(name, bytes, ref size, 0, 0) == 0 ? Encoding.UTF8.GetString(bytes, 0, (int)size).TrimEnd('\0') : "";
    }

    // The first count numbers of a struct of 64-bit ones (vm.swapusage)
    public static long[]? Longs(string name, int count)
    {
        var values = new long[count];
        nint size = count * sizeof(long);
        return SysctlByName(name, values, ref size, 0, 0) == 0 ? values : null;
    }

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, ref long value, ref nint size, nint newValue, nint newSize);

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, [Out] byte[]? value, ref nint size, nint newValue, nint newSize);

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, [Out] long[] value, ref nint size, nint newValue, nint newSize);
}
