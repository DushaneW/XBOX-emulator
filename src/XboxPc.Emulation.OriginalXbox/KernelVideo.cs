using XboxPc.Emulation.Gpu.Nv2a;

namespace XboxPc.Emulation.OriginalXbox;

/// <summary>
/// Video kernel exports (Av*). The real kernel programs the CRTC and the TV encoder over several AvSetDisplayMode
/// steps; this HLE programs the NV2A display model in one step. Mode numbers -> resolutions come from nxdk's public
/// mode table (lib/hal/video.c). The emulated console's AV configuration (cable pack, region, enabled HD modes) is a
/// machine setting, see <see cref="OriginalXboxMachine.VideoEncoderSettings"/>.
/// </summary>
public sealed partial class KernelHle
{
    private uint _savedDataAddress;
    public bool TvEncoderVideoEnabled { get; private set; } = true;
    public int FlickerFilter { get; private set; }
    public bool SoftenFilter { get; private set; }

    private static readonly Dictionary<uint, (int W, int H, int Hz)> VideoModes = new()
    {
        [0x04010101] = (640, 480, 60), [0x04010103] = (640, 480, 60), [0x0401010B] = (640, 480, 60),
        [0x04020202] = (720, 480, 60), [0x04020204] = (720, 480, 60), [0x0402020C] = (720, 480, 60),
        [0x08010119] = (640, 480, 60), [0x20010101] = (640, 480, 60), [0x20010103] = (640, 480, 60),
        [0x2001010B] = (640, 480, 60), [0x20020202] = (720, 480, 60), [0x20020204] = (720, 480, 60),
        [0x2002020C] = (720, 480, 60), [0x44030307] = (640, 480, 50), [0x44040408] = (720, 480, 50),
        [0x48030314] = (640, 480, 50), [0x48040415] = (720, 480, 50), [0x60030307] = (640, 480, 50),
        [0x60040408] = (720, 480, 50), [0x88070701] = (640, 480, 60), [0x88080801] = (720, 480, 60),
        [0x880B0A02] = (1280, 720, 60), [0x880E0C03] = (1920, 1080, 60), [0xC0060601] = (640, 480, 60),
        [0xC0030303] = (800, 600, 60), [0xC0040404] = (1024, 768, 60),
    };

    private void RegisterVideoExports()
    {
        _handlers[1] = () => Return(_savedDataAddress);                          // AvGetSavedDataAddress
        _handlers[4] = () => { _savedDataAddress = Arg(0); Return(0); };       // AvSetSavedDataAddress
        _handlers[2] = AvSendTVEncoderOption;
        _handlers[3] = AvSetDisplayMode;
    }

    // ULONG AvSetDisplayMode(PVOID RegisterBase, ULONG Step, ULONG Mode, ULONG Format, ULONG Pitch, ULONG FrameBuffer)
    private void AvSetDisplayMode()
    {
        uint mode = Arg(2), format = Arg(3), pitch = Arg(4), framebuffer = Arg(5);
        if (!VideoModes.TryGetValue(mode, out var m)) throw Unsupported($"AvSetDisplayMode with unknown mode 0x{mode:X8}");
        if (!Enum.IsDefined((ScanoutFormat)format)) throw Unsupported($"AvSetDisplayMode with unknown format 0x{format:X}");
        _m.Gpu.SetDisplayMode(m.W, m.H, (int)pitch, (ScanoutFormat)format, framebuffer, m.Hz);
        Return(0); // 0 = no further steps
    }

    // VOID AvSendTVEncoderOption(PVOID RegisterBase, ULONG Option, ULONG Param, PULONG Result)
    private void AvSendTVEncoderOption()
    {
        uint option = Arg(1), param = Arg(2), result = Arg(3);
        switch (option)
        {
            case 6: if (result != 0) Bus.Write32(result, _m.VideoEncoderSettings); break; // VIDEO_ENC_GET_SETTINGS
            case 9: TvEncoderVideoEnabled = param == 0; _m.Gpu.OutputEnabled = TvEncoderVideoEnabled; break; // VIDEOENABLE: param TRUE = disable
            case 11: FlickerFilter = (int)(param & 7); break;                                             // FLICKERFILTER (no visual effect modelled)
            case 14: SoftenFilter = param != 0; break;                                                   // SOFTEN_FILTER (no visual effect modelled)
            default: throw Unsupported($"AvSendTVEncoderOption option {option}");
        }
        Return(0);
    }
}
