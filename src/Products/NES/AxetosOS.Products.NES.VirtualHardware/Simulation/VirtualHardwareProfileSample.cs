using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AxetosOS.Products.NES.VirtualHardware.Simulation;

/// <summary>
/// Named internal timing buckets used only by the opt-in diagnostics path.
/// These are not hardware signals and are never stored by a physical package.
/// </summary>
public enum VirtualHardwareProfileSection : byte
{
    Rp2A03ControllerIo,
    Rp2A03Apu,
    Rp2A03CpuCore,
    Rp2A03Dma,
    Rp2C02CpuPort,
    Rp2C02Raster,
    Rp2C02Vram,
    Rp2C02Background,
    Rp2C02Sprite,
    Rp2C02VideoOutput,
    Rp2C02PackageOutputs
}

/// <summary>
/// Ephemeral sampled-profile handle passed only through the diagnostics call
/// path. Physical packages retain no profiler/simulator reference between
/// reactions, and normal execution never constructs this value.
/// </summary>
public readonly struct VirtualHardwareProfileSample
{
    private readonly VirtualHardwareSimulator? _simulator;
    private readonly int _componentIndex;

    internal VirtualHardwareProfileSample(VirtualHardwareSimulator simulator, int componentIndex)
    {
        _simulator = simulator;
        _componentIndex = componentIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long BeginSection() => Stopwatch.GetTimestamp();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndSection(VirtualHardwareProfileSection section, long started)
    {
        if (_simulator is null) return;
        _simulator.RecordProfileSection(
            _componentIndex,
            section,
            Stopwatch.GetTimestamp() - started);
    }
}
