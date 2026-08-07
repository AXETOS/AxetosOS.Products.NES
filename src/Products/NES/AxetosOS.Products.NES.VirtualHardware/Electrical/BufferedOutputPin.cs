using System.Runtime.CompilerServices;

namespace AxetosOS.Products.NES.VirtualHardware.Electrical;

/// <summary>
/// A package-owned output pin for retained non-binary samples such as a video
/// pixel or DAC level. The package only drives the pin. An external connector
/// may optionally capture and drain samples without callbacks into the package.
///
/// Capture uses a reusable ring buffer so high-rate video output does not pay
/// Queue&lt;T&gt; enqueue/dequeue overhead for every pixel.
/// </summary>
public sealed class BufferedOutputPin<T>
{
    private T[] _captured = new T[256];
    private int _head;
    private int _count;

    public BufferedOutputPin(string name, T initialValue = default!)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        CurrentValue = initialValue;
    }

    public string Name { get; }
    public T CurrentValue { get; private set; }
    public bool CaptureEnabled { get; private set; }
    public ulong DriveCount { get; private set; }
    public int CapturedCount => _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Drive(T value)
    {
        CurrentValue = value;
        DriveCount++;
        if (!CaptureEnabled) return;

        if (_count == _captured.Length) Grow();
        var tail = _head + _count;
        if (tail >= _captured.Length) tail -= _captured.Length;
        _captured[tail] = value;
        _count++;
    }

    public void SetCaptureEnabled(bool enabled, bool clearCaptured = true)
    {
        CaptureEnabled = enabled;
        if (clearCaptured) ClearCaptured();
    }

    public int Drain(Action<T> receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        var count = 0;
        while (_count != 0)
        {
            receiver(RemoveFirst());
            count++;
        }
        return count;
    }

    /// <summary>
    /// Copies captured samples in bulk into caller-owned storage. This is the
    /// preferred host presentation path for high-rate video/audio output.
    /// </summary>
    public int Drain(Span<T> destination)
    {
        var count = Math.Min(destination.Length, _count);
        if (count == 0) return 0;

        var first = Math.Min(count, _captured.Length - _head);
        _captured.AsSpan(_head, first).CopyTo(destination);
        var second = count - first;
        if (second != 0)
            _captured.AsSpan(0, second).CopyTo(destination[first..]);

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(_captured, _head, first);
            if (second != 0) Array.Clear(_captured, 0, second);
        }

        _head += count;
        if (_head >= _captured.Length) _head -= _captured.Length;
        _count -= count;
        if (_count == 0) _head = 0;
        return count;
    }

    public T[] Drain()
    {
        if (_count == 0) return [];
        var values = new T[_count];
        Drain(values.AsSpan());
        return values;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T RemoveFirst()
    {
        var value = _captured[_head];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) _captured[_head] = default!;
        _head++;
        if (_head == _captured.Length) _head = 0;
        _count--;
        if (_count == 0) _head = 0;
        return value;
    }

    private void Grow()
    {
        var expanded = new T[_captured.Length * 2];
        var first = Math.Min(_count, _captured.Length - _head);
        _captured.AsSpan(_head, first).CopyTo(expanded);
        if (_count > first)
            _captured.AsSpan(0, _count - first).CopyTo(expanded.AsSpan(first));
        _captured = expanded;
        _head = 0;
    }

    private void ClearCaptured()
    {
        if (_count != 0 && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            var first = Math.Min(_count, _captured.Length - _head);
            Array.Clear(_captured, _head, first);
            if (_count > first) Array.Clear(_captured, 0, _count - first);
        }
        _head = 0;
        _count = 0;
    }
}
