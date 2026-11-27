using System.IO;
using System.Runtime.InteropServices;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.App.Services.Media;

/// <summary>
/// Reads a video file's frames with Media Foundation's Source Reader: Windows' own media stack, so whatever Windows
/// plays works (MP4/H.264, MOV, WMV, AVI; HEVC, VP9 or AV1 where their Store extensions are installed) and nothing is
/// shipped. Frames come as 32-bit RGB (the reader's built-in YUV → RGB conversion), synchronously, on the calling
/// thread; audio is never read. One thread only (the cooler screen player's), which is a plain MTA thread.
/// </summary>
/// <remarks>
/// Chosen over WPF's MediaPlayer + VideoDrawing + RenderTargetBitmap, which needs the UI thread (or a second
/// dispatcher), renders video into a RenderTargetBitmap unreliably (black frames on some GPUs, because the video is
/// composed by the hardware) and has no frame-accurate access. The Source Reader is a small COM interop (five
/// interfaces, four functions), off the UI thread, frame-accurate and loops by seeking to 0. Windows N editions
/// without the Media Feature Pack have no Media Foundation: opening a video then fails with a clear message.
/// </remarks>
internal sealed class VideoFileReader : IDisposable
{
    /// <summary>MF_VERSION for Windows 7 and later (MF_SDK_VERSION 2 &lt;&lt; 16 | MF_API_VERSION 0x70).</summary>
    private const int MfVersion = 0x00020070;

    private const int FirstVideoStream = unchecked((int)0xFFFFFFFC);
    private const int AllStreams = unchecked((int)0xFFFFFFFE);
    private const int ErrorFlag = 0x1;
    private const int EndOfStreamFlag = 0x2;
    private const int CurrentMediaTypeChangedFlag = 0x20;

    /// <summary>PROPVARIANT type of a 64-bit integer (a position in 100 ns units).</summary>
    private const ushort VtI8 = 20;

    /// <summary>A read that returns no frame this many times in a row (only stream ticks) counts as the end.</summary>
    private const int MaxEmptyReads = 100;

    private const int MaxSide = 8192;

    private static readonly Guid MfSourceReaderEnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MfMtDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MfVideoFormatRgb32 = new("00000016-0000-0010-8000-00aa00389b71");

    /// <summary>MFStartup once per process (it counts calls; the process exit releases it).</summary>
    private static readonly Lazy<int> Startup = new(() => MFStartup(MfVersion, 0), LazyThreadSafetyMode.ExecutionAndPublication);

    private IMFSourceReader? _reader;
    private int _width;
    private int _height;
    private int _stride;

    /// <exception cref="IOException">Media Foundation can't open the file, or it has no video Windows can decode.</exception>
    public VideoFileReader(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            Check(Startup.Value, "start Media Foundation");
            Check(MFCreateAttributes(out var attributes, 1), "create the reader's attributes");
            IMFSourceReader reader;
            try
            {
                var key = MfSourceReaderEnableVideoProcessing;
                Check(attributes.SetUINT32(ref key, 1), "enable video processing");
                Check(MFCreateSourceReaderFromURL(path, attributes, out reader), "open the video");
                _reader = reader;
            }
            finally
            {
                Marshal.ReleaseComObject(attributes);
            }

            Check(reader.SetStreamSelection(AllStreams, 0), "deselect the streams");
            Check(reader.SetStreamSelection(FirstVideoStream, 1), "select the video stream");
            Check(MFCreateMediaType(out var type), "create a media type");
            try
            {
                var major = MfMtMajorType;
                var video = MfMediaTypeVideo;
                var subtype = MfMtSubtype;
                var rgb32 = MfVideoFormatRgb32;
                Check(type.SetGUID(ref major, ref video), "set the major type");
                Check(type.SetGUID(ref subtype, ref rgb32), "set the subtype");
                Check(reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, type), "decode the video to RGB");
            }
            finally
            {
                Marshal.ReleaseComObject(type);
            }

            ReadFormat(reader);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException)
        {
            Dispose();
            throw new IOException($"Windows can't play this video ({ex.Message}).", ex);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Width => _width;

    public int Height => _height;

    /// <summary>
    /// Reads the next frame into <paramref name="frame"/> (made, or remade at a new size, as needed); false at the end
    /// of the file. Its fourth byte is undefined (RGB32): draw it as opaque.
    /// </summary>
    /// <exception cref="IOException">Decoding failed.</exception>
    public bool TryRead(ref BgraImage? frame, out TimeSpan timestamp)
    {
        var reader = _reader ?? throw new ObjectDisposedException(nameof(VideoFileReader));
        for (var empty = 0; empty < MaxEmptyReads; empty++)
        {
            int flags;
            long time;
            IMFSample? sample;
            try
            {
                Check(reader.ReadSample(FirstVideoStream, 0, out _, out flags, out time, out sample), "read a frame");
            }
            catch (COMException ex)
            {
                throw new IOException($"Windows couldn't decode the video ({ex.Message}).", ex);
            }

            try
            {
                if ((flags & ErrorFlag) != 0)
                {
                    throw new IOException("Windows couldn't decode the video.");
                }

                if ((flags & CurrentMediaTypeChangedFlag) != 0)
                {
                    ReadFormat(reader);
                }

                if ((flags & EndOfStreamFlag) != 0)
                {
                    timestamp = default;
                    return false;
                }

                if (sample is null)
                {
                    continue; // a gap in the stream
                }

                timestamp = TimeSpan.FromTicks(time); // 100 ns units, like TimeSpan
                Copy(sample, ref frame);
                return true;
            }
            finally
            {
                if (sample is not null)
                {
                    Marshal.ReleaseComObject(sample); // at once: decoders have only a few samples to hand out
                }
            }
        }

        timestamp = default;
        return false;
    }

    /// <summary>Back to the start, for the next loop.</summary>
    /// <exception cref="IOException">The file can't seek.</exception>
    public void Rewind()
    {
        var reader = _reader ?? throw new ObjectDisposedException(nameof(VideoFileReader));
        var format = Guid.Empty; // 100 ns units
        var position = new PropVariant { Type = VtI8, Value = 0 };
        try
        {
            Check(reader.SetCurrentPosition(ref format, ref position), "go back to the start");
        }
        catch (COMException ex)
        {
            throw new IOException($"The video can't go back to its start ({ex.Message}).", ex);
        }
    }

    public void Dispose()
    {
        if (_reader is { } reader)
        {
            _reader = null;
            Marshal.ReleaseComObject(reader);
        }
    }

    private static void Check(int hresult, string what)
    {
        if (hresult < 0)
        {
            throw new COMException($"Couldn't {what} (0x{hresult:X8})", hresult);
        }
    }

    private void ReadFormat(IMFSourceReader reader)
    {
        Check(reader.GetCurrentMediaType(FirstVideoStream, out var current), "read the video format");
        try
        {
            var sizeKey = MfMtFrameSize;
            Check(current.GetUINT64(ref sizeKey, out var size), "read the frame size");
            _width = (int)(size >> 32);
            _height = (int)(size & 0xFFFFFFFF);
            var strideKey = MfMtDefaultStride;
            _stride = current.GetUINT32(ref strideKey, out var stride) >= 0 ? unchecked((int)stride) : _width * 4;
        }
        finally
        {
            Marshal.ReleaseComObject(current);
        }

        if (_width is < 1 or > MaxSide || _height is < 1 or > MaxSide)
        {
            throw new IOException($"The video's frame size ({_width} × {_height}) isn't supported.");
        }
    }

    /// <summary>Copies the sample's picture, top row first, into <paramref name="frame"/>.</summary>
    private void Copy(IMFSample sample, ref BgraImage? frame)
    {
        if (frame is null || frame.Width != _width || frame.Height != _height)
        {
            frame = new BgraImage(_width, _height);
        }

        Check(sample.ConvertToContiguousBuffer(out var buffer), "read the frame");
        try
        {
            Check(buffer.Lock(out var data, out _, out var length), "read the frame");
            try
            {
                var rowBytes = _width * 4;
                var stride = _stride == 0 ? rowBytes : _stride;
                var step = Math.Abs(stride);
                if (step < rowBytes || ((long)step * (_height - 1)) + rowBytes > length)
                {
                    throw new IOException("The video frame is smaller than its size says.");
                }

                // A negative stride is a bottom-up picture: its first row in memory is the bottom one.
                for (var y = 0; y < _height; y++)
                {
                    var row = stride > 0 ? y : _height - 1 - y;
                    Marshal.Copy(data + (row * step), frame.Pixels, y * rowBytes, rowBytes);
                }
            }
            finally
            {
                _ = buffer.Unlock();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes attributes, out IMFSourceReader reader);

    /// <summary>A PROPVARIANT holding a 64-bit integer (24 bytes on x64, of which the first 16 are read).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public long Value;
    }

    /// <summary>IMFAttributes: all 30 methods in vtable order; only the ones used have real signatures.</summary>
    [ComImport]
    [Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig]
        int GetItem();

        [PreserveSig]
        int GetItemType();

        [PreserveSig]
        int CompareItem();

        [PreserveSig]
        int Compare();

        [PreserveSig]
        int GetUINT32(ref Guid key, out uint value);

        [PreserveSig]
        int GetUINT64(ref Guid key, out ulong value);

        [PreserveSig]
        int GetDouble();

        [PreserveSig]
        int GetGUID();

        [PreserveSig]
        int GetStringLength();

        [PreserveSig]
        int GetString();

        [PreserveSig]
        int GetAllocatedString();

        [PreserveSig]
        int GetBlobSize();

        [PreserveSig]
        int GetBlob();

        [PreserveSig]
        int GetAllocatedBlob();

        [PreserveSig]
        int GetUnknown();

        [PreserveSig]
        int SetItem();

        [PreserveSig]
        int DeleteItem();

        [PreserveSig]
        int DeleteAllItems();

        [PreserveSig]
        int SetUINT32(ref Guid key, uint value);

        [PreserveSig]
        int SetUINT64();

        [PreserveSig]
        int SetDouble();

        [PreserveSig]
        int SetGUID(ref Guid key, ref Guid value);

        [PreserveSig]
        int SetString();

        [PreserveSig]
        int SetBlob();

        [PreserveSig]
        int SetUnknown();

        [PreserveSig]
        int LockStore();

        [PreserveSig]
        int UnlockStore();

        [PreserveSig]
        int GetCount();

        [PreserveSig]
        int GetItemByIndex();

        [PreserveSig]
        int CopyAllItems();
    }

    /// <summary>IMFMediaType: IMFAttributes' 30 methods (COM interop doesn't inherit them), then its own 5.</summary>
    [ComImport]
    [Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        [PreserveSig]
        int GetItem();

        [PreserveSig]
        int GetItemType();

        [PreserveSig]
        int CompareItem();

        [PreserveSig]
        int Compare();

        [PreserveSig]
        int GetUINT32(ref Guid key, out uint value);

        [PreserveSig]
        int GetUINT64(ref Guid key, out ulong value);

        [PreserveSig]
        int GetDouble();

        [PreserveSig]
        int GetGUID();

        [PreserveSig]
        int GetStringLength();

        [PreserveSig]
        int GetString();

        [PreserveSig]
        int GetAllocatedString();

        [PreserveSig]
        int GetBlobSize();

        [PreserveSig]
        int GetBlob();

        [PreserveSig]
        int GetAllocatedBlob();

        [PreserveSig]
        int GetUnknown();

        [PreserveSig]
        int SetItem();

        [PreserveSig]
        int DeleteItem();

        [PreserveSig]
        int DeleteAllItems();

        [PreserveSig]
        int SetUINT32(ref Guid key, uint value);

        [PreserveSig]
        int SetUINT64();

        [PreserveSig]
        int SetDouble();

        [PreserveSig]
        int SetGUID(ref Guid key, ref Guid value);

        [PreserveSig]
        int SetString();

        [PreserveSig]
        int SetBlob();

        [PreserveSig]
        int SetUnknown();

        [PreserveSig]
        int LockStore();

        [PreserveSig]
        int UnlockStore();

        [PreserveSig]
        int GetCount();

        [PreserveSig]
        int GetItemByIndex();

        [PreserveSig]
        int CopyAllItems();

        [PreserveSig]
        int GetMajorType();

        [PreserveSig]
        int IsCompressedFormat();

        [PreserveSig]
        int IsEqual();

        [PreserveSig]
        int GetRepresentation();

        [PreserveSig]
        int FreeRepresentation();
    }

    /// <summary>IMFSample: IMFAttributes' 30 methods, then its own 14.</summary>
    [ComImport]
    [Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        [PreserveSig]
        int GetItem();

        [PreserveSig]
        int GetItemType();

        [PreserveSig]
        int CompareItem();

        [PreserveSig]
        int Compare();

        [PreserveSig]
        int GetUINT32(ref Guid key, out uint value);

        [PreserveSig]
        int GetUINT64(ref Guid key, out ulong value);

        [PreserveSig]
        int GetDouble();

        [PreserveSig]
        int GetGUID();

        [PreserveSig]
        int GetStringLength();

        [PreserveSig]
        int GetString();

        [PreserveSig]
        int GetAllocatedString();

        [PreserveSig]
        int GetBlobSize();

        [PreserveSig]
        int GetBlob();

        [PreserveSig]
        int GetAllocatedBlob();

        [PreserveSig]
        int GetUnknown();

        [PreserveSig]
        int SetItem();

        [PreserveSig]
        int DeleteItem();

        [PreserveSig]
        int DeleteAllItems();

        [PreserveSig]
        int SetUINT32(ref Guid key, uint value);

        [PreserveSig]
        int SetUINT64();

        [PreserveSig]
        int SetDouble();

        [PreserveSig]
        int SetGUID(ref Guid key, ref Guid value);

        [PreserveSig]
        int SetString();

        [PreserveSig]
        int SetBlob();

        [PreserveSig]
        int SetUnknown();

        [PreserveSig]
        int LockStore();

        [PreserveSig]
        int UnlockStore();

        [PreserveSig]
        int GetCount();

        [PreserveSig]
        int GetItemByIndex();

        [PreserveSig]
        int CopyAllItems();

        [PreserveSig]
        int GetSampleFlags();

        [PreserveSig]
        int SetSampleFlags();

        [PreserveSig]
        int GetSampleTime();

        [PreserveSig]
        int SetSampleTime();

        [PreserveSig]
        int GetSampleDuration();

        [PreserveSig]
        int SetSampleDuration();

        [PreserveSig]
        int GetBufferCount();

        [PreserveSig]
        int GetBufferByIndex();

        [PreserveSig]
        int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);

        [PreserveSig]
        int AddBuffer();

        [PreserveSig]
        int RemoveBufferByIndex();

        [PreserveSig]
        int RemoveAllBuffers();

        [PreserveSig]
        int GetTotalLength();

        [PreserveSig]
        int CopyToBuffer();
    }

    /// <summary>IMFMediaBuffer.</summary>
    [ComImport]
    [Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig]
        int Lock(out IntPtr data, out int maxLength, out int currentLength);

        [PreserveSig]
        int Unlock();

        [PreserveSig]
        int GetCurrentLength();

        [PreserveSig]
        int SetCurrentLength();

        [PreserveSig]
        int GetMaxLength();
    }

    /// <summary>IMFSourceReader.</summary>
    [ComImport]
    [Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        [PreserveSig]
        int GetStreamSelection();

        [PreserveSig]
        int SetStreamSelection(int streamIndex, int selected);

        [PreserveSig]
        int GetNativeMediaType();

        [PreserveSig]
        int GetCurrentMediaType(int streamIndex, out IMFMediaType mediaType);

        [PreserveSig]
        int SetCurrentMediaType(int streamIndex, IntPtr reserved, IMFMediaType mediaType);

        [PreserveSig]
        int SetCurrentPosition(ref Guid timeFormat, ref PropVariant position);

        [PreserveSig]
        int ReadSample(int streamIndex, int controlFlags, out int actualStreamIndex, out int streamFlags, out long timestamp, out IMFSample? sample);

        [PreserveSig]
        int Flush();

        [PreserveSig]
        int GetServiceForStream();

        [PreserveSig]
        int GetPresentationAttribute();
    }
}
