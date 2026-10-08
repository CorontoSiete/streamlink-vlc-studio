using System.Security.Cryptography;
using System.Text;

namespace StreamlinkVlcStudio.Maintenance;

// The PowerShell installer uses the same mutex identity. The lease stays on the
// calling thread and is released by Windows if that process exits unexpectedly.
internal sealed class InstallationOperationLease : IDisposable
{
    private readonly Mutex mutex;
    private bool disposed;

    private InstallationOperationLease(Mutex mutex) => this.mutex = mutex;

    internal static string GetMutexName(string directory)
    {
        var identity = Encoding.UTF8.GetBytes(PathSafety.Normalize(directory).ToUpperInvariant());
        return "Local\\StreamStudio.Installation." + Convert.ToHexString(SHA256.HashData(identity));
    }

    internal static InstallationOperationLease Acquire(string directory)
    {
        var mutex = new Mutex(false, GetMutexName(directory));
        try
        {
            var acquired = false;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                throw new IOException("Another installation or uninstall is using this application folder. Wait for it to finish and try again.");
            return new InstallationOperationLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { mutex.ReleaseMutex(); }
        finally { mutex.Dispose(); }
    }
}
