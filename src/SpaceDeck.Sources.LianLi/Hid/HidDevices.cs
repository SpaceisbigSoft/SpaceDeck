using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SpaceDeck.Sources.LianLi.Hid;

/// <summary>One HID top-level collection (Windows opens each collection of a USB device as its own path).</summary>
public sealed record HidCollection(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort VersionNumber,
    ushort UsagePage,
    ushort Usage,
    int InputReportLength,
    int OutputReportLength,
    int FeatureReportLength,
    string? Product)
{
    /// <summary>Usage pages 0xFF00–0xFFFF are vendor-defined: where coolers and fan hubs put their command channel.</summary>
    public bool IsVendorDefined => UsagePage >= 0xFF00;
}

/// <summary>Finds HID collections without opening them for reading or writing.</summary>
public static class HidDevices
{
    /// <summary>Every present HID collection of <paramref name="vendorId"/>. Collections that can't be queried are skipped.</summary>
    public static IReadOnlyList<HidCollection> Find(ushort vendorId)
    {
        HidNative.HidD_GetHidGuid(out var hidGuid);
        var set = HidNative.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, HidNative.DigcfPresent | HidNative.DigcfDeviceInterface);
        if (set == HidNative.InvalidHandleValue)
        {
            return [];
        }

        var found = new List<HidCollection>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new HidNative.SpDeviceInterfaceData { CbSize = Marshal.SizeOf<HidNative.SpDeviceInterfaceData>() };
                if (!HidNative.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data))
                {
                    break; // ERROR_NO_MORE_ITEMS, or the list went away: either way, done
                }

                var path = InterfacePath(set, ref data);
                if (path is not null && TryDescribe(path, vendorId) is { } collection)
                {
                    found.Add(collection);
                }
            }
        }
        finally
        {
            HidNative.SetupDiDestroyDeviceInfoList(set);
        }

        return found;
    }

    private static string? InterfacePath(IntPtr set, ref HidNative.SpDeviceInterfaceData data)
    {
        // First call: ask for the size (fails with ERROR_INSUFFICIENT_BUFFER by design).
        HidNative.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0)
        {
            return null;
        }

        var detail = Marshal.AllocHGlobal((int)required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is the size of the fixed part: 8 on 64-bit, 6 on 32-bit.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!HidNative.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, IntPtr.Zero))
            {
                return null;
            }

            return Marshal.PtrToStringUni(detail + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    private static HidCollection? TryDescribe(string path, ushort vendorId)
    {
        // Access 0: enough to read attributes and capabilities, and never conflicts with the device's owner.
        using var handle = HidNative.CreateFile(path, 0, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var attributes = new HidNative.HiddAttributes { Size = Marshal.SizeOf<HidNative.HiddAttributes>() };
        if (!HidNative.HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != vendorId)
        {
            return null;
        }

        if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed))
        {
            return null;
        }

        HidNative.HidpCaps caps;
        try
        {
            if (HidNative.HidP_GetCaps(preparsed, out caps) != HidNative.HidpStatusSuccess)
            {
                return null;
            }
        }
        finally
        {
            HidNative.HidD_FreePreparsedData(preparsed);
        }

        return new HidCollection(path, attributes.VendorId, attributes.ProductId, attributes.VersionNumber, caps.UsagePage, caps.Usage,
            caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength, ProductString(handle));
    }

    private static string? ProductString(SafeFileHandle handle)
    {
        var buffer = new byte[256];
        if (!HidNative.HidD_GetProductString(handle, buffer, buffer.Length))
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(buffer);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        text = (end >= 0 ? text[..end] : text).Trim();
        return text.Length > 0 ? text : null;
    }
}
