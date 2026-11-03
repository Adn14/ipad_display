using System.Runtime.InteropServices;

namespace IPadScreenHost;

/// <summary>
/// Registers the current thread with the Multimedia Class Scheduler Service for the lifetime of the
/// scope. MMCSS is Windows' supported way to keep latency-critical threads (games, pro audio, capture)
/// scheduled promptly under load: it boosts them into the real-time priority range while active.
///
/// This replaces "pinning" a thread to a core. Thread.BeginThreadAffinity() doesn't pin anything (it
/// only tells the CLR host the managed thread must stay on its OS thread), and hard-pinning with
/// SetThreadAffinityMask is counter-productive on hybrid CPUs: the thread can end up stuck on an
/// efficiency core or behind other work on its one allowed core.
/// </summary>
internal readonly struct MmcssScope : IDisposable
{
    private readonly IntPtr _handle;

    private MmcssScope(IntPtr handle) => _handle = handle;

    /// <param name="task">An MMCSS task name, e.g. "Games", "Playback", "Pro Audio" (HKLM\...\Multimedia\SystemProfile\Tasks).</param>
    public static MmcssScope Enter(string task)
    {
        uint index = 0;
        IntPtr h = Native.AvSetMmThreadCharacteristicsW(task, ref index);
        if (h == IntPtr.Zero) Log.Debug($"MMCSS '{task}' unavailable (Win32 error {Marshal.GetLastWin32Error()}); using normal scheduling.");
        return new MmcssScope(h);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) Native.AvRevertMmThreadCharacteristics(_handle);
    }
}
