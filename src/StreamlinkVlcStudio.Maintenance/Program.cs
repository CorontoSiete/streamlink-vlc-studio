namespace StreamlinkVlcStudio.Maintenance;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        return MaintenanceApplication.Run(args);
    }
}
