using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AxetosOS.Products.NES.Host.Windows;

public sealed class Win32WaveOutAudioSink : IAudioSampleSink
{
    private const uint WaveMapper = uint.MaxValue;
    private const uint WhdrDone = 0x00000001;
    private const uint CallbackNull = 0;

    // Small fixed packets reduce latency while a short prebuffer absorbs normal
    // frame-pacing jitter from the emulator host.
    private const int BufferSamples = 512;
    private const int BufferCount = 8;
    private const int PrebufferCount = 3;

    private readonly BlockingCollection<short[]> _queue = new(new ConcurrentQueue<short[]>(), 32);
    private readonly List<NativeBuffer> _buffers = [];
    private Thread? _worker;
    private IntPtr _waveOut;
    private volatile bool _running;
    private bool _disposed;
    private long _bufferedSamples;
    private long _droppedSamples;

    public Win32WaveOutAudioSink(int sampleRate, int channels = 1)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels));
        SampleRate = sampleRate;
        Channels = channels;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public double BufferedMilliseconds => Math.Max(0, Interlocked.Read(ref _bufferedSamples)) * 1000.0 / (SampleRate * Channels);
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    public void Start()
    {
        ThrowIfDisposed();
        if (_running) return;

        var format = new WaveFormatEx
        {
            FormatTag = 1,
            Channels = (ushort)Channels,
            SamplesPerSec = (uint)SampleRate,
            BitsPerSample = 16,
            BlockAlign = (ushort)(Channels * sizeof(short)),
            AvgBytesPerSec = (uint)(SampleRate * Channels * sizeof(short)),
            Size = 0
        };

        Check(waveOutOpen(out _waveOut, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, CallbackNull), "waveOutOpen");
        for (var index = 0; index < BufferCount; index++)
        {
            _buffers.Add(new NativeBuffer(BufferSamples * Channels));
        }

        _running = true;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "AxetosOS Native Audio",
            Priority = ThreadPriority.AboveNormal
        };
        _worker.Start();
    }

    public void Submit(ReadOnlySpan<float> samples)
    {
        ThrowIfDisposed();
        if (!_running || samples.IsEmpty) return;

        var pcm = new short[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = Math.Clamp(samples[index], -1.0f, 1.0f);
            pcm[index] = (short)Math.Round(sample * short.MaxValue);
        }

        // Never block the emulator thread. The queue is intentionally large
        // enough for normal timing jitter, while TryAdd prevents a stalled audio
        // device from freezing video and input.
        if (_queue.TryAdd(pcm))
        {
            Interlocked.Add(ref _bufferedSamples, pcm.Length);
        }
        else
        {
            Interlocked.Add(ref _droppedSamples, pcm.Length);
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _queue.CompleteAdding();
        _worker?.Join(TimeSpan.FromSeconds(2));
        _worker = null;

        if (_waveOut != IntPtr.Zero)
        {
            waveOutReset(_waveOut);
            foreach (var buffer in _buffers)
            {
                buffer.Unprepare(_waveOut);
                buffer.Dispose();
            }
            _buffers.Clear();
            waveOutClose(_waveOut);
            _waveOut = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _queue.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void WorkerLoop()
    {
        var pending = new Queue<short>();
        var packetSamples = BufferSamples * Channels;
        var prebufferSamples = packetSamples * PrebufferCount;
        var playbackStarted = false;

        while (_running || !_queue.IsCompleted || pending.Count > 0)
        {
            while (_queue.TryTake(out var samples, playbackStarted ? 2 : 10))
            {
                foreach (var sample in samples)
                {
                    pending.Enqueue(sample);
                }

                if (!playbackStarted && pending.Count >= prebufferSamples)
                {
                    playbackStarted = true;
                    break;
                }

                if (playbackStarted && pending.Count >= packetSamples * BufferCount)
                {
                    break;
                }
            }

            if (!playbackStarted)
            {
                if (!_running && pending.Count > 0)
                {
                    playbackStarted = true;
                }
                else
                {
                    Thread.Sleep(1);
                    continue;
                }
            }

            var wroteBuffer = false;
            foreach (var buffer in _buffers)
            {
                var completed = buffer.ReclaimCompleted();
                if (completed > 0)
                {
                    Interlocked.Add(ref _bufferedSamples, -completed);
                }
                if (!buffer.IsAvailable) continue;

                // During normal playback only submit complete, equally sized
                // packets. A short final packet is allowed only while stopping.
                if (pending.Count < packetSamples && _running) break;
                if (pending.Count == 0) break;

                var count = Math.Min(packetSamples, pending.Count);
                buffer.Fill(pending, count);
                buffer.Write(_waveOut, count);
                wroteBuffer = true;
            }

            if (!wroteBuffer)
            {
                Thread.Sleep(1);
            }
        }
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"{operation} failed with MMRESULT {result}.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class NativeBuffer : IDisposable
    {
        private readonly IntPtr _data;
        private readonly IntPtr _header;
        private readonly short[] _copyBuffer;
        private bool _prepared;
        private int _submittedSamples;

        public NativeBuffer(int capacity)
        {
            Capacity = capacity;
            _copyBuffer = new short[capacity];
            _data = Marshal.AllocHGlobal(capacity * sizeof(short));
            _header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
            Marshal.StructureToPtr(new WaveHeader { Data = _data }, _header, false);
        }

        public int Capacity { get; }

        public bool IsAvailable
        {
            get
            {
                if (!_prepared) return true;
                var header = Marshal.PtrToStructure<WaveHeader>(_header);
                return (header.Flags & WhdrDone) != 0;
            }
        }

        public int ReclaimCompleted()
        {
            if (!_prepared || _submittedSamples == 0) return 0;
            var header = Marshal.PtrToStructure<WaveHeader>(_header);
            if ((header.Flags & WhdrDone) == 0) return 0;
            var completed = _submittedSamples;
            _submittedSamples = 0;
            return completed;
        }

        public void Fill(Queue<short> source, int count)
        {
            Array.Clear(_copyBuffer);
            for (var index = 0; index < count; index++)
            {
                _copyBuffer[index] = source.Dequeue();
            }
            Marshal.Copy(_copyBuffer, 0, _data, Capacity);
        }

        public void Write(IntPtr waveOut, int count)
        {
            if (_prepared)
            {
                Check(waveOutUnprepareHeader(waveOut, _header, (uint)Marshal.SizeOf<WaveHeader>()), "waveOutUnprepareHeader");
                _prepared = false;
            }

            var header = Marshal.PtrToStructure<WaveHeader>(_header);
            header.Data = _data;
            header.BufferLength = (uint)(count * sizeof(short));
            header.BytesRecorded = 0;
            header.Flags = 0;
            header.Loops = 0;
            Marshal.StructureToPtr(header, _header, false);

            Check(waveOutPrepareHeader(waveOut, _header, (uint)Marshal.SizeOf<WaveHeader>()), "waveOutPrepareHeader");
            _prepared = true;
            _submittedSamples = count;
            Check(waveOutWrite(waveOut, _header, (uint)Marshal.SizeOf<WaveHeader>()), "waveOutWrite");
        }

        public void Unprepare(IntPtr waveOut)
        {
            if (!_prepared) return;
            waveOutUnprepareHeader(waveOut, _header, (uint)Marshal.SizeOf<WaveHeader>());
            _prepared = false;
            _submittedSamples = 0;
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(_header);
            Marshal.FreeHGlobal(_data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutOpen(out IntPtr waveOut, uint deviceId, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern uint waveOutPrepareHeader(IntPtr waveOut, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutUnprepareHeader(IntPtr waveOut, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutWrite(IntPtr waveOut, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutReset(IntPtr waveOut);

    [DllImport("winmm.dll")]
    private static extern uint waveOutClose(IntPtr waveOut);
}
