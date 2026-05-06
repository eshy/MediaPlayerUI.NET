using ManagedBass;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace HanumanInstitute.MediaPlayer.Avalonia.Bass;

// Helper that maps Stream → BASS FileProcedures callbacks
internal class StreamWrapper : IDisposable
{
    internal static int CreateFromStream(Stream inputStream, BassFlags flags = BassFlags.Default)
    {
        if (!inputStream.CanSeek)
            throw new ArgumentException("Stream must be seekable for BASS streaming");

        if (!inputStream.CanRead)
            throw new ArgumentException("Stream must be readable");

        // Keep stream alive as long as the BASS handle exists
        // (BASS calls our callbacks multiple times → do NOT dispose early)
        var streamWrapper = new StreamWrapper(inputStream);

        var gcHandle = GCHandle.Alloc(streamWrapper, GCHandleType.Pinned);
        streamWrapper._gcHandle = gcHandle;

        var procs = new FileProcedures
        {
            Close  = streamWrapper.Close,
            Length = streamWrapper.Length,
            Read   = streamWrapper.Read,
            Seek   = streamWrapper.Seek
        };

        var handle = ManagedBass.Bass.CreateStream(
            StreamSystem.NoBuffer,          // or StreamSystem.Buffer if you want BASS buffering
            flags,
            procs,
            gcHandle.AddrOfPinnedObject());                   // user pointer — can pass streamWrapper if you want to retrieve it later

        if (handle == 0)
        {
            // cleanup on failure
            streamWrapper.Dispose();
            throw new Exception($"BASS error: {ManagedBass.Bass.LastError}");
        }

        // Optional: attach a sync to free wrapper when stream is freed
        ManagedBass.Bass.ChannelSetSync(handle, SyncFlags.Free, 0,
            (h, channel, data, user) => ((StreamWrapper)GCHandle.FromIntPtr((IntPtr)user).Target).Dispose(),
            gcHandle.AddrOfPinnedObject());

        return handle;
    }
    
    private readonly Stream _stream;
    private bool _disposed;
    private GCHandle _gcHandle;

    public StreamWrapper(Stream stream) => _stream = stream;

    public long Length(IntPtr user) => _stream.Length;

    public bool Seek(long offset, IntPtr user)
    {
        try
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public int Read(IntPtr buffer, int length, IntPtr user)
    {
        try
        {
            var bytes = new byte[length];
            int read = _stream.Read(bytes, 0, length);

            if (read > 0)
                Marshal.Copy(bytes, 0, buffer, read);

            return read;
        }
        catch
        {
            return -1; // error signal to BASS
        }
    }

    public void Close(IntPtr user)
    {
        // BASS calls this when stream is freed → safe to dispose here
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_gcHandle.IsAllocated) _gcHandle.Free();
        _stream?.Dispose();
    }
}
