
internal sealed class WasapiLoopbackCapture : IDisposable
{
    private const uint AudioClientStreamFlagsLoopback = 0x00020000;
    private const uint AudioClientBufferFlagsSilent = 0x00000002;
    private const uint ClsContextAll = 0x17;
    private const long CaptureBufferDurationHns = 10_000_000;
    private static readonly Guid MmDeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MmDeviceEnumeratorInterfaceId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid AudioClientInterfaceId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid AudioCaptureClientInterfaceId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid IeeeFloatSubformat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid PcmSubformat = new("00000001-0000-0010-8000-00AA00389B71");

    private readonly ConcurrentQueue<AudioPacket> packets = new();
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? worker;
    private Exception? failure;

    private WasapiLoopbackCapture()
    {
    }

    internal static async Task<WasapiLoopbackCapture> StartAsync()
    {
        var capture = new WasapiLoopbackCapture();
        capture.worker = Task.Factory.StartNew(
            capture.CaptureLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            await capture.started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return capture;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    internal AudioPacket[] GetPacketsSince(long timestamp) =>
        packets.Where(packet => packet.Timestamp >= timestamp).ToArray();

    internal AudioPacket[] GetPacketsBefore(long timestamp, TimeSpan window)
    {
        var windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
        return packets.Where(packet => packet.Timestamp >= timestamp - windowTicks && packet.Timestamp < timestamp).ToArray();
    }

    internal void EnsureHealthy()
    {
        if (Volatile.Read(ref failure) is { } exception)
        {
            throw new InvalidOperationException("WASAPI loopback capture failed.", exception);
        }
    }

    private void CaptureLoop()
    {
        var comInitialized = false;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? endpoint = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        IntPtr mixFormatPointer = IntPtr.Zero;
        try
        {
            var initializeResult = CoInitializeEx(IntPtr.Zero, 0);
            ThrowOnFailure(initializeResult, "CoInitializeEx");
            comInitialized = true;

            var classId = MmDeviceEnumeratorClassId;
            var interfaceId = MmDeviceEnumeratorInterfaceId;
            ThrowOnFailure(
                CoCreateInstance(ref classId, IntPtr.Zero, ClsContextAll, ref interfaceId, out enumerator),
                "CoCreateInstance(MMDeviceEnumerator)");
            ThrowOnFailure(enumerator.GetDefaultAudioEndpoint(dataFlow: 0, role: 1, out endpoint), "GetDefaultAudioEndpoint");

            interfaceId = AudioClientInterfaceId;
            ThrowOnFailure(endpoint.Activate(ref interfaceId, ClsContextAll, IntPtr.Zero, out audioClient), "IMMDevice.Activate(IAudioClient)");
            ThrowOnFailure(audioClient.GetMixFormat(out mixFormatPointer), "IAudioClient.GetMixFormat");
            var format = WasapiAudioFormat.Read(mixFormatPointer);
            var sessionId = Guid.Empty;
            ThrowOnFailure(
                audioClient.Initialize(
                    shareMode: 0,
                    streamFlags: AudioClientStreamFlagsLoopback,
                    bufferDuration: CaptureBufferDurationHns,
                    periodicity: 0,
                    format: mixFormatPointer,
                    audioSessionGuid: ref sessionId),
                "IAudioClient.Initialize(loopback)");
            Marshal.FreeCoTaskMem(mixFormatPointer);
            mixFormatPointer = IntPtr.Zero;

            interfaceId = AudioCaptureClientInterfaceId;
            ThrowOnFailure(audioClient.GetService(ref interfaceId, out captureClient), "IAudioClient.GetService(IAudioCaptureClient)");
            ThrowOnFailure(audioClient.Start(), "IAudioClient.Start");
            started.TrySetResult(true);

            while (!stop.IsCancellationRequested)
            {
                ThrowOnFailure(captureClient.GetNextPacketSize(out var nextPacketFrames), "IAudioCaptureClient.GetNextPacketSize");
                if (nextPacketFrames == 0)
                {
                    Thread.Sleep(2);
                    continue;
                }

                ThrowOnFailure(
                    captureClient.GetBuffer(out var data, out var frameCount, out var flags, out _, out _),
                    "IAudioCaptureClient.GetBuffer");
                try
                {
                    var timestamp = Stopwatch.GetTimestamp();
                    packets.Enqueue(format.Measure(data, frameCount, (flags & AudioClientBufferFlagsSilent) != 0, timestamp));
                }
                finally
                {
                    ThrowOnFailure(captureClient.ReleaseBuffer(frameCount), "IAudioCaptureClient.ReleaseBuffer");
                }
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref failure, exception);
            started.TrySetException(exception);
        }
        finally
        {
            if (mixFormatPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(mixFormatPointer);
            }

            if (audioClient is not null)
            {
                _ = audioClient.Stop();
            }

            ReleaseComObject(captureClient);
            ReleaseComObject(audioClient);
            ReleaseComObject(endpoint);
            ReleaseComObject(enumerator);
            if (comInitialized)
            {
                CoUninitialize();
            }
        }
    }

    private static void ThrowOnFailure(int result, string operation)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result, new IntPtr(-1));
            throw new InvalidOperationException($"{operation} failed with HRESULT 0x{result:X8}.");
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        try
        {
            worker?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        stop.Dispose();
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint context,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IMMDeviceEnumerator instance);

    private sealed record WasapiAudioFormat(int Channels, int SampleRate, int BitsPerSample, int BytesPerSample, int BlockAlign, bool IsFloat)
    {
        internal static WasapiAudioFormat Read(IntPtr format)
        {
            var formatTag = unchecked((ushort)Marshal.ReadInt16(format, 0));
            var channels = Marshal.ReadInt16(format, 2);
            var sampleRate = Marshal.ReadInt32(format, 4);
            var blockAlign = Marshal.ReadInt16(format, 12);
            var bitsPerSample = Marshal.ReadInt16(format, 14);
            var bytesPerSample = (bitsPerSample + 7) / 8;
            var isFloat = formatTag == 3;
            if (formatTag == 0xFFFE)
            {
                var subFormat = Marshal.PtrToStructure<Guid>(IntPtr.Add(format, 24));
                isFloat = subFormat == IeeeFloatSubformat;
                formatTag = subFormat == PcmSubformat ? (ushort)1 : isFloat ? (ushort)3 : (ushort)0;
            }

            if (channels <= 0 || sampleRate <= 0 || blockAlign <= 0 || bytesPerSample <= 0 ||
                (formatTag != 1 && formatTag != 3) || (isFloat && bitsPerSample != 32) ||
                blockAlign < channels * bytesPerSample)
            {
                throw new NotSupportedException(
                    $"Unsupported WASAPI loopback format: tag={formatTag}, channels={channels}, rate={sampleRate}, bits={bitsPerSample}, blockAlign={blockAlign}.");
            }

            return new WasapiAudioFormat(channels, sampleRate, bitsPerSample, bytesPerSample, blockAlign, isFloat);
        }

        internal AudioPacket Measure(IntPtr data, uint frameCount, bool isSilent, long timestamp)
        {
            if (frameCount == 0 || isSilent || data == IntPtr.Zero)
            {
                return new AudioPacket(timestamp, checked((int)frameCount), SampleRate, Rms: 0, ToneMagnitude: 0, IsSilent: true);
            }

            var byteCount = checked((int)frameCount * BlockAlign);
            var bytes = new byte[byteCount];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            var sumSquares = 0d;
            var previous = 0d;
            var previous2 = 0d;
            var peak = 0d;
            var coefficient = 2 * Math.Cos(2 * Math.PI * 440 / SampleRate);
            for (var frame = 0; frame < frameCount; frame++)
            {
                var frameOffset = frame * BlockAlign;
                var mono = 0d;
                for (var channel = 0; channel < Channels; channel++)
                {
                    mono += ReadSample(bytes, frameOffset + channel * BytesPerSample);
                }

                mono /= Channels;
                sumSquares += mono * mono;
                peak = Math.Max(peak, Math.Abs(mono));
                var current = mono + coefficient * previous - previous2;
                previous2 = previous;
                previous = current;
            }

            var frames = checked((int)frameCount);
            var power = Math.Max(0, previous * previous + previous2 * previous2 - coefficient * previous * previous2);
            return new AudioPacket(
                timestamp,
                frames,
                SampleRate,
                Math.Sqrt(sumSquares / frames),
                2 * Math.Sqrt(power) / frames,
                peak == 0);
        }

        private double ReadSample(byte[] bytes, int offset)
        {
            if (IsFloat)
            {
                return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)));
            }

            return BitsPerSample switch
            {
                8 => (bytes[offset] - 128) / 128d,
                16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset, 2)) / 32768d,
                24 => Read24BitPcm(bytes, offset) / 8388608d,
                32 => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)) / 2147483648d,
                _ => throw new NotSupportedException($"Unsupported WASAPI PCM depth: {BitsPerSample} bits.")
            };
        }

        private static int Read24BitPcm(byte[] bytes, int offset)
        {
            var value = bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;
            return (value & 0x00800000) == 0 ? value : value | unchecked((int)0xFF000000);
        }
    }

    internal readonly record struct AudioPacket(long Timestamp, int Frames, int SampleRate, double Rms, double ToneMagnitude, bool IsSilent);

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice endpoint);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint context, IntPtr activationParameters, [MarshalAs(UnmanagedType.Interface)] out IAudioClient audioClient);
        [PreserveSig] int OpenPropertyStore(uint accessMode, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, ref Guid audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatchFormat);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IAudioCaptureClient service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint framesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint framesRead);
        [PreserveSig] int GetNextPacketSize(out uint framesInNextPacket);
    }
}
