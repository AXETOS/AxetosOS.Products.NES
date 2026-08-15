using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;

namespace AxetosOS.Products.NES.Desktop;

/// <summary>
/// Receives only RP2C02 pixel output. Application chrome is never written here,
/// so recording/export can consume this stream without capturing menus or status UI.
/// </summary>
internal sealed class NesFramebufferOutput : IVirtualNesVideoSink
{
    private const int Width = 256;
    private const int Height = 240;
    private static readonly uint[] Palette = BuildPalette();
    private static readonly uint[] EmphasizedPalette = BuildEmphasizedPalette();
    private uint[] _renderPixels = new uint[Width * Height];
    private uint[] _completedPixels = new uint[Width * Height];

    public ReadOnlyMemory<uint> CompletedPixels => _completedPixels;
    public ulong CompletedFrame { get; private set; } = ulong.MaxValue;


    public NesFramebufferState CaptureState() => new(
        (uint[])_renderPixels.Clone(),
        (uint[])_completedPixels.Clone(),
        CompletedFrame);

    public void RestoreState(NesFramebufferState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.RenderPixels.Length != _renderPixels.Length ||
            state.CompletedPixels.Length != _completedPixels.Length)
        {
            throw new InvalidOperationException("NES framebuffer state has an incompatible geometry.");
        }

        state.RenderPixels.CopyTo(_renderPixels, 0);
        state.CompletedPixels.CopyTo(_completedPixels, 0);
        CompletedFrame = state.CompletedFrame;
    }

    /// <summary>
    /// Raised synchronously when a complete NES frame is published. Consumers
    /// that retain a frame beyond the callback must copy it because buffers rotate.
    /// </summary>
    public event Action<ulong, ReadOnlyMemory<uint>>? FrameCompleted;

    public void AcceptPixel(ulong frame, int x, int y, byte colorCode, byte emphasis)
    {
        if ((uint)x >= Width || (uint)y >= Height) return;
        _renderPixels[(y * Width) + x] = EmphasizedPalette[((emphasis & 0x07) << 6) | (colorCode & 0x3F)];
        PublishFrameIfComplete(frame, x, y);
    }

    public void AcceptPixels(ReadOnlySpan<RicohVideoPixelSample> samples)
    {
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            if ((uint)sample.X >= Width || (uint)sample.Y >= Height) continue;
            _renderPixels[(sample.Y * Width) + sample.X] =
                EmphasizedPalette[((sample.Emphasis & 0x07) << 6) | (sample.ColorCode & 0x3F)];
            PublishFrameIfComplete(sample.Frame, sample.X, sample.Y);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PublishFrameIfComplete(ulong frame, int x, int y)
    {
        if (x != Width - 1 || y != Height - 1 || frame == CompletedFrame) return;

        (_renderPixels, _completedPixels) = (_completedPixels, _renderPixels);
        CompletedFrame = frame;
        FrameCompleted?.Invoke(frame, _completedPixels);
    }

    private static uint[] BuildPalette()
    {
        int[] rgb =
        [
            0x666666,0x002A88,0x1412A7,0x3B00A4,0x5C007E,0x6E0040,0x6C0600,0x561D00,
            0x333500,0x0B4800,0x005200,0x004F08,0x00404D,0x000000,0x000000,0x000000,
            0xADADAD,0x155FD9,0x4240FF,0x7527FE,0xA01ACC,0xB71E7B,0xB53120,0x994E00,
            0x6B6D00,0x388700,0x0C9300,0x008F32,0x007C8D,0x000000,0x000000,0x000000,
            0xFFFEFF,0x64B0FF,0x9290FF,0xC676FF,0xF36AFF,0xFE6ECC,0xFE8170,0xEA9E22,
            0xBCBE00,0x88D800,0x5CE430,0x45E082,0x48CDDE,0x4F4F4F,0x000000,0x000000,
            0xFFFEFF,0xC0DFFF,0xD3D2FF,0xE8C8FF,0xFBC2FF,0xFEC4EA,0xFECCC5,0xF7D8A5,
            0xE4E594,0xCFEF96,0xBDF4AB,0xB3F3CC,0xB5EBF2,0xB8B8B8,0x000000,0x000000
        ];
        return rgb.Select(value => 0xFF000000u | (uint)value).ToArray();
    }

    private static uint[] BuildEmphasizedPalette()
    {
        var values = new uint[8 * 64];
        for (var emphasis = 0; emphasis < 8; emphasis++)
        {
            for (var color = 0; color < 64; color++)
            {
                values[(emphasis << 6) | color] = ApplyEmphasis(Palette[color], (byte)emphasis);
            }
        }
        return values;
    }

    private static uint ApplyEmphasis(uint argb, byte emphasis)
    {
        if ((emphasis & 0x07) == 0) return argb;
        var r = (int)((argb >> 16) & 0xFF);
        var g = (int)((argb >> 8) & 0xFF);
        var b = (int)(argb & 0xFF);
        if ((emphasis & 0x01) != 0) { g = g * 3 / 4; b = b * 3 / 4; }
        if ((emphasis & 0x02) != 0) { r = r * 3 / 4; b = b * 3 / 4; }
        if ((emphasis & 0x04) != 0) { r = r * 3 / 4; g = g * 3 / 4; }
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }
}

internal sealed record NesFramebufferState(uint[] RenderPixels, uint[] CompletedPixels, ulong CompletedFrame);
