using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceDeck.Sources.LianLi.Hid;

/// <summary>
/// Listens to a HID collection's input reports on its own read-only handle, without ever sending anything.
/// </summary>
/// <remarks>
/// A request/response device (like the Galahad II) only sends a report when some program asks for one, so this sees
/// replies only while another program (FanControl's Lian Li plugin) is polling, and only if Windows hands each open
/// handle its own copy of every input report. Finding out whether it does is what <c>spacedeck-cli lianli-listen</c>
/// is for.
/// </remarks>
public sealed class HidPassiveReader : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly int _reportLength;

    private HidPassiveReader(SafeFileHandle handle, HidCollection collection)
    {
        _handle = handle;
        _reportLength = collection.InputReportLength;
        Collection = collection;
    }

    public HidCollection Collection { get; }

    /// <summary>Opens <paramref name="collection"/> for reading only, shared with every other reader and writer.</summary>
    /// <exception cref="Win32Exception">The collection can't be opened (unplugged, or held exclusively).</exception>
    public static HidPassiveReader Open(HidCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.InputReportLength <= 0)
        {
            throw new ArgumentException("This collection has no input reports.", nameof(collection));
        }

        var handle = HidNative.CreateFile(collection.Path, HidNative.GenericRead, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open,
            HidNative.FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        return new HidPassiveReader(handle, collection);
    }

    /// <summary>
    /// Reads reports until <paramref name="token"/> is cancelled, calling <paramref name="onReport"/> with each one
    /// (on this thread). Returns normally on cancellation.
    /// </summary>
    /// <exception cref="Win32Exception">The read failed (device unplugged, handle closed).</exception>
    public void Run(Action<byte[]> onReport, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(onReport);
        var buffer = Marshal.AllocHGlobal(_reportLength);
        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        using var completed = new ManualResetEvent(false);
        try
        {
            while (!token.IsCancellationRequested)
            {
                completed.Reset();
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = completed.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
                if (!HidNative.ReadFile(_handle, buffer, (uint)_reportLength, IntPtr.Zero, overlapped))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != HidNative.ErrorIoPending)
                    {
                        throw new Win32Exception(error);
                    }
                }

                // Wait for a report or for the caller to stop; a pending read must finish (or be cancelled) before the
                // buffer and OVERLAPPED are freed.
                if (WaitHandle.WaitAny([completed, token.WaitHandle]) == 1)
                {
                    HidNative.CancelIoEx(_handle, overlapped);
                    HidNative.GetOverlappedResult(_handle, overlapped, out _, wait: true);
                    return;
                }

                if (!HidNative.GetOverlappedResult(_handle, overlapped, out var read, wait: false))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == HidNative.ErrorOperationAborted)
                    {
                        return;
                    }

                    throw new Win32Exception(error);
                }

                if (read > 0)
                {
                    var report = new byte[read];
                    Marshal.Copy(buffer, report, 0, (int)read);
                    onReport(report);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(overlapped);
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose() => _handle.Dispose();
}
