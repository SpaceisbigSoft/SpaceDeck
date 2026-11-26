using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceDeck.Sources.LianLi.Hid;

/// <summary>
/// One open HID collection: reads its input reports and, when opened with <see cref="OpenForQueries"/>, sends the
/// Galahad II's query packets and nothing else.
/// </summary>
/// <remarks>
/// <see cref="OpenReadOnly"/> listens without ever sending: on a request/response device (like the Galahad II) it only
/// sees replies while another program polls, and only if Windows hands each handle its own copy of every input
/// report. <see cref="OpenForQueries"/> lets SpaceDeck ask the status itself, for when no other program drives the
/// device (owner's rule, 2026-11-26). Both share the device with every other reader and writer.
/// </remarks>
public sealed class HidChannel : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly Lock _sendLock = new();

    private HidChannel(SafeFileHandle handle, HidCollection collection, bool canSend)
    {
        _handle = handle;
        Collection = collection;
        CanSend = canSend;
    }

    public HidCollection Collection { get; }

    /// <summary>True when opened with <see cref="OpenForQueries"/>.</summary>
    public bool CanSend { get; }

    /// <summary>Opens <paramref name="collection"/> for reading only.</summary>
    /// <exception cref="Win32Exception">The collection can't be opened (unplugged, or held exclusively).</exception>
    public static HidChannel OpenReadOnly(HidCollection collection) => Open(collection, HidNative.GenericRead, canSend: false);

    /// <summary>Opens <paramref name="collection"/> for reading and for sending query packets.</summary>
    /// <exception cref="Win32Exception">The collection can't be opened (unplugged, or held exclusively).</exception>
    public static HidChannel OpenForQueries(HidCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.OutputReportLength < GalahadProtocol.CommandPacketLength)
        {
            throw new ArgumentException("This collection takes no command packets.", nameof(collection));
        }

        return Open(collection, HidNative.GenericRead | HidNative.GenericWrite, canSend: true);
    }

    private static HidChannel Open(HidCollection collection, uint access, bool canSend)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.InputReportLength <= 0)
        {
            throw new ArgumentException("This collection has no input reports.", nameof(collection));
        }

        var handle = HidNative.CreateFile(collection.Path, access, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open,
            HidNative.FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        return new HidChannel(handle, collection, canSend);
    }

    /// <summary>
    /// Reads reports until <paramref name="token"/> is cancelled, calling <paramref name="onReport"/> with each one
    /// (on this thread). Returns normally on cancellation.
    /// </summary>
    /// <exception cref="Win32Exception">The read failed (device unplugged, handle closed).</exception>
    public void Run(Action<byte[]> onReport, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(onReport);
        var length = Collection.InputReportLength;
        var buffer = Marshal.AllocHGlobal(length);
        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        using var completed = new ManualResetEvent(false);
        try
        {
            while (!token.IsCancellationRequested)
            {
                completed.Reset();
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = completed.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
                if (!HidNative.ReadFile(_handle, buffer, (uint)length, IntPtr.Zero, overlapped))
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

    /// <summary>
    /// Sends one query packet. Refuses anything <see cref="GalahadProtocol.IsQuery"/> doesn't accept, and anything at
    /// all on a read-only channel. Returns false when the device didn't take it within <paramref name="timeout"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel is read-only, or the packet isn't a query.</exception>
    /// <exception cref="Win32Exception">The write failed (device unplugged).</exception>
    public bool Send(byte[] report, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!CanSend)
        {
            throw new InvalidOperationException("This channel was opened read-only.");
        }

        if (report.Length != Collection.OutputReportLength || !GalahadProtocol.IsQuery(report))
        {
            throw new InvalidOperationException("Only the cooler's status and firmware queries may be sent.");
        }

        lock (_sendLock)
        {
            var buffer = Marshal.AllocHGlobal(report.Length);
            var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            using var completed = new ManualResetEvent(false);
            try
            {
                Marshal.Copy(report, 0, buffer, report.Length);
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = completed.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
                if (!HidNative.WriteFile(_handle, buffer, (uint)report.Length, IntPtr.Zero, overlapped))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != HidNative.ErrorIoPending)
                    {
                        throw new Win32Exception(error);
                    }
                }

                if (!completed.WaitOne(timeout))
                {
                    HidNative.CancelIoEx(_handle, overlapped);
                    HidNative.GetOverlappedResult(_handle, overlapped, out _, wait: true);
                    return false;
                }

                if (!HidNative.GetOverlappedResult(_handle, overlapped, out _, wait: false))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(overlapped);
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public void Dispose() => _handle.Dispose();
}
