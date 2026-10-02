using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal static partial class LibVlcNative
{
    internal static IReadOnlySet<(string Name, string Capability)> ReadModuleCapabilities()
    {
        var list = module_list_get(out var count);
        if (list == nint.Zero) throw new InvalidDataException("VLC could not enumerate its loaded modules.");
        try
        {
            // Bound enumeration before calculating native pointer offsets.
            if (count > 16_384) throw new InvalidDataException("VLC returned too many modules for dependency verification.");
            var modules = new HashSet<(string Name, string Capability)>();
            for (var index = 0; index < (int)count; index++)
            {
                var module = Marshal.ReadIntPtr(list, checked(index * IntPtr.Size));
                if (module == nint.Zero) throw new InvalidDataException("VLC returned an empty module entry.");
                var name = Marshal.PtrToStringUTF8(module_get_object(module));
                var capability = Marshal.PtrToStringUTF8(module_get_capability(module));
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(capability)) modules.Add((name, capability));
            }
            return modules;
        }
        finally { module_list_free(list); }
    }

    [DllImport("libvlccore", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint module_list_get(out nuint count);

    [DllImport("libvlccore", CallingConvention = CallingConvention.Cdecl)]
    private static extern void module_list_free(nint list);

    [DllImport("libvlccore", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint module_get_object(nint module);

    [DllImport("libvlccore", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint module_get_capability(nint module);
}
