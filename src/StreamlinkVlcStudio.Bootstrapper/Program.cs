using WixToolset.BootstrapperApplicationApi;

namespace StreamlinkVlcStudio.Bootstrapper;

internal static class Program
{
    private static int Main()
    {
        ManagedBootstrapperApplication.Run(new StudioBootstrapperApplication());
        return 0;
    }
}
