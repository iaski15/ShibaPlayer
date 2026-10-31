using System.IO;
using System.Runtime.InteropServices;

namespace ShibaPlayer;

/// <summary>Turns any file our Decoder can read into an MP3, using Windows' built-in MP3 encoder (Media Foundation sink writer).</summary>
public static unsafe class Mp3Encoder
{
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid Mp3 = new("00000055-0000-0010-8000-00aa00389b71");
    private static readonly Guid NumChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid SamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid AvgBytes = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid BlockAlign = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid MediaTypeId = new("44ae0fa8-ea31-4109-8d2e-4cae4997c555");

    public static void Encode(string input, string output, int kbps)
    {
        using var decoder = new Decoder(input);
        int channels = decoder.Channels, rate = decoder.SampleRate;

        var outType = FindMp3Type(channels, rate, kbps * 1000 / 8);
        IntPtr writer = IntPtr.Zero, inType = IntPtr.Zero;
        try
        {
            Check(MFCreateSinkWriterFromURL(output, IntPtr.Zero, IntPtr.Zero, out writer));
            uint stream;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Decoder.Slot(writer, 3))(writer, outType, &stream)); // AddStream

            Check(MFCreateMediaType(out inType));
            SetGuid(inType, MajorType, Audio);
            SetGuid(inType, SubType, Pcm);
            SetUInt32(inType, NumChannels, (uint)channels);
            SetUInt32(inType, SamplesPerSecond, (uint)rate);
            SetUInt32(inType, BitsPerSample, 16);
            SetUInt32(inType, BlockAlign, (uint)(2 * channels));
            SetUInt32(inType, AvgBytes, (uint)(2 * channels * rate));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Decoder.Slot(writer, 4))(writer, stream, inType, IntPtr.Zero)); // SetInputMediaType
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Decoder.Slot(writer, 5))(writer)); // BeginWriting

            long frames = 0;
            while (decoder.Read() is { } chunk)
            {
                if (chunk.Length == 0) continue;
                WriteChunk(writer, stream, chunk, frames, channels, rate);
                frames += chunk.Length / channels;
            }
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Decoder.Slot(writer, 11))(writer)); // Finalize
        }
        finally
        {
            Decoder.Release(inType);
            Decoder.Release(outType);
            Decoder.Release(writer);
        }
    }

    private static void WriteChunk(IntPtr writer, uint stream, float[] chunk, long frames, int channels, int rate)
    {
        Check(MFCreateMemoryBuffer((uint)(chunk.Length * 2), out var buffer));
        IntPtr sample = IntPtr.Zero;
        try
        {
            byte* data;
            uint max, length;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Decoder.Slot(buffer, 3))(buffer, &data, &max, &length)); // Lock
            var pcm = (short*)data;
            for (int i = 0; i < chunk.Length; i++) pcm[i] = (short)(Math.Clamp(chunk[i], -1f, 1f) * short.MaxValue);
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Decoder.Slot(buffer, 4))(buffer); // Unlock
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Decoder.Slot(buffer, 6))(buffer, (uint)(chunk.Length * 2))); // SetCurrentLength

            Check(MFCreateSample(out sample));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Decoder.Slot(sample, 42))(sample, buffer)); // AddBuffer
            long start = frames * TimeSpan.TicksPerSecond / rate, length100ns = chunk.Length / channels * TimeSpan.TicksPerSecond / rate;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Decoder.Slot(sample, 36))(sample, start)); // SetSampleTime
            Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Decoder.Slot(sample, 38))(sample, length100ns)); // SetSampleDuration
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Decoder.Slot(writer, 6))(writer, stream, sample)); // WriteSample
        }
        finally
        {
            Decoder.Release(sample);
            Decoder.Release(buffer);
        }
    }

    /// <summary>The encoder only takes output types it listed itself: finds the one closest to, and not above, the wanted bitrate.</summary>
    private static IntPtr FindMp3Type(int channels, int rate, int bytesPerSecond)
    {
        var subtype = Mp3;
        IntPtr list;
        Check(MFTranscodeGetAudioOutputAvailableTypes(&subtype, 0x3F, IntPtr.Zero, &list)); // MFT_ENUM_FLAG_ALL
        try
        {
            uint count;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Decoder.Slot(list, 3))(list, &count)); // GetElementCount
            IntPtr best = IntPtr.Zero;
            uint bestBytes = 0;
            for (uint i = 0; i < count; i++)
            {
                IntPtr element, type;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Decoder.Slot(list, 4))(list, i, &element)); // GetElement
                var iid = MediaTypeId;
                var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Decoder.Slot(element, 0))(element, &iid, &type); // QueryInterface
                Decoder.Release(element);
                Check(hr);

                var bytes = Decoder.GetUInt32(type, AvgBytes);
                if (Decoder.GetUInt32(type, NumChannels) == channels && Decoder.GetUInt32(type, SamplesPerSecond) == rate
                    && bytes <= bytesPerSecond && bytes > bestBytes)
                {
                    Decoder.Release(best);
                    best = type;
                    bestBytes = bytes;
                }
                else Decoder.Release(type);
            }
            return best != IntPtr.Zero ? best
                : throw new IOException($"Windows' MP3 encoder can't handle {channels} channels at {rate} Hz.");
        }
        finally { Decoder.Release(list); }
    }

    private static void SetGuid(IntPtr attrs, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Decoder.Slot(attrs, 24))(attrs, &key, &value));

    private static void SetUInt32(IntPtr attrs, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Decoder.Slot(attrs, 21))(attrs, &key, value));

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMemoryBuffer(uint size, out IntPtr buffer);
    [DllImport("mf.dll", ExactSpelling = true)] private static extern int MFTranscodeGetAudioOutputAvailableTypes(Guid* subtype, uint flags, IntPtr attributes, IntPtr* types);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IntPtr writer);
}
