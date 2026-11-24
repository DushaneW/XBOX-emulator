using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using XboxPc.Emulation.Core;
using XboxPc.Emulation.Gpu.Nv2a;
using RawRect = Vortice.RawRect;
using SrvDimension = Vortice.Direct3D12.ShaderResourceViewDimension;

namespace XboxPc.Emulation.Gpu.D3D12;

/// <summary>
/// Direct3D 12 renderer for the NV2A. Receives the Kelvin engine's clears and draws (see <see cref="IRenderBackend"/>)
/// and renders them on the host GPU at <see cref="Scale"/> times the guest resolution: render targets and depth
/// buffers are host textures keyed by their guest address; textures are decoded from guest memory once and cached by
/// content; the register combiners run as generated pixel shaders (<see cref="ShaderGenerator"/>); blending,
/// depth and stencil map onto pipeline state. The CPU reference renderer still writes guest memory, so programs that
/// read their own images back keep working; this backend supplies the displayed picture.
/// All methods run on the emulation thread. Any Direct3D failure disables the backend and the CPU picture is used.
/// </summary>
public sealed unsafe class D3D12Backend : IRenderBackend, IRamWatcher, IDisposable
{
    private const int CbBytes = 512;
    private const int SrvRing = 16384;
    private const int SamplerRing = 2048;
    private const long UploadBytes = 64L << 20;

    private readonly ID3D12Device _device;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12GraphicsCommandList _list;
    private readonly ID3D12Fence _fence;
    private readonly ID3D12RootSignature _root;
    private readonly ID3D12DescriptorHeap _srvHeap, _samplerHeap, _rtvHeap, _dsvHeap;
    private readonly uint _srvInc, _samplerInc, _rtvInc, _dsvInc;
    private readonly ID3D12Resource _upload;
    private readonly byte* _uploadPtr;
    private readonly ReadOnlyMemory<byte> _vs;
    private readonly Dictionary<PixelShaderKey, ReadOnlyMemory<byte>> _pixelShaders = [];
    private readonly Dictionary<string, ID3D12PipelineState> _pipelines = [];
    private readonly Dictionary<(uint Address, int Pitch), Target> _colorTargets = [];
    private readonly Dictionary<uint, Target> _depthTargets = [];
    private readonly Dictionary<(uint, uint, int, int, int), CachedTexture> _textures = [];
    private readonly List<IDisposable> _pendingRelease = [];
    private readonly Queue<int> _freeRtv = new(), _freeDsv = new();
    private PhysicalBus? _memory;
    private Nv2aDevice? _gpu;
    private readonly bool _logicOpSupported;
    private ulong _fenceValue;
    private long _uploadOffset;
    private int _srvNext, _samplerNext;
    private bool _open = true;
    private bool _disposed;

    private long CurrentVBlank => _gpu?.VBlankCount ?? 0;

    private sealed class Target
    {
        public required ID3D12Resource Resource;
        public required int Handle;
        public required int GuestWidth, GuestHeight, AaX, AaY;
        public required bool Depth;
        public required Format Format;
        public ResourceStates State;
        public long LastWriteVBlank = -1;
        public bool Stale;
        public int HostWidth, HostHeight;

        // guest surface (colour targets)
        public uint Address;
        public int Pitch, Bpp = 4;
        public uint GuestFormat;
        public bool Swizzled;
        public int SwizzleWidth, SwizzleHeight;
        public uint Bytes;
        public uint RamOffset;
        public bool Watched;
        public Owner Owner = Owner.Memory;
    }

    private readonly HashSet<string> _reported = [];
    private readonly List<string> _messages = [];

    /// <summary>Limitations hit so far (each reported once).</summary>
    public IReadOnlyList<string> Messages => _messages;

    /// <summary>One line per colour target: guest address, pitch, size, anti-aliasing, owner (diagnostics).</summary>
    public IReadOnlyList<string> DescribeTargets() =>
        [.. _colorTargets.Values.Select(t => $"0x{t.Address:X8} pitch {t.Pitch} {t.GuestWidth}x{t.GuestHeight} aa {t.AaX}x{t.AaY} fmt {t.GuestFormat} {(t.Swizzled ? "swizzled " : "")}owner {t.Owner} watched {t.Watched}")];

    /// <summary>Event log of ownership changes (diagnostics, last 200).</summary>
    public IReadOnlyList<string> Trace => _trace;

    private readonly List<string> _trace = [];

    private void Log(string line)
    {
        if (_trace.Count >= 200) _trace.RemoveAt(0);
        _trace.Add(line);
    }

    private void ReportOnce(string message)
    {
        if (_reported.Add(message)) _messages.Add(message);
    }

    private sealed class CachedTexture
    {
        public required ID3D12Resource Resource;
        public ulong Hash;
        public long LastUse;
    }

    private D3D12Backend(ID3D12Device device, string adapterName, int scale, bool ownsMemory)
    {
        _device = device;
        _ownsMemory = ownsMemory;
        AdapterName = adapterName;
        Scale = scale;
        _queue = device.CreateCommandQueue(CommandListType.Direct, CommandQueuePriority.Normal, CommandQueueFlags.None, 0);
        _allocator = device.CreateCommandAllocator(CommandListType.Direct);
        _list = device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, _allocator, null!);
        _fence = device.CreateFence(0, FenceFlags.None);

        var srvDesc = new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, SrvRing, DescriptorHeapFlags.ShaderVisible, 0);
        _srvHeap = device.CreateDescriptorHeap(in srvDesc);
        var samplerDesc = new DescriptorHeapDescription(DescriptorHeapType.Sampler, SamplerRing, DescriptorHeapFlags.ShaderVisible, 0);
        _samplerHeap = device.CreateDescriptorHeap(in samplerDesc);
        var rtvDesc = new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 512, DescriptorHeapFlags.None, 0);
        _rtvHeap = device.CreateDescriptorHeap(in rtvDesc);
        var dsvDesc = new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 128, DescriptorHeapFlags.None, 0);
        _dsvHeap = device.CreateDescriptorHeap(in dsvDesc);
        _srvInc = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        _samplerInc = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler);
        _rtvInc = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        _dsvInc = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
        for (var i = 0; i < 256; i++) _freeRtv.Enqueue(i);
        for (var i = 0; i < 128; i++) _freeDsv.Enqueue(i);

        _upload = device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer((ulong)UploadBytes, ResourceFlags.None, 0), ResourceStates.GenericRead, null);
        void* p;
        _upload.Map(0, &p).CheckError();
        _uploadPtr = (byte*)p;

        // b0: constants (both stages); t0-t3 textures; s0-s3 samplers
        var root = new RootSignatureDescription1(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            [
                new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0, RootDescriptorFlags.None), ShaderVisibility.All),
                new RootParameter1(new RootDescriptorTable1([new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, 0, 0, 0, DescriptorRangeFlags.DataVolatile | DescriptorRangeFlags.DescriptorsVolatile)]), ShaderVisibility.Pixel),
                new RootParameter1(new RootDescriptorTable1([new DescriptorRange1(DescriptorRangeType.Sampler, 4, 0, 0, 0, DescriptorRangeFlags.DescriptorsVolatile)]), ShaderVisibility.Pixel),
            ],
            []);
        _root = device.CreateRootSignature(in root);
        _vs = Compile(ShaderGenerator.VertexShader, "vs_5_0", "nv2a-vs");
        _logicOpSupported = device.Options.OutputMergerLogicOp;

        // scaled copy (guest pixels into a host target): full-screen triangle sampling t0 with point filtering
        _scaledCopy = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _root,
            VertexShader = Compile(ScaledCopyVs, "vs_5_0", "copy-vs"),
            PixelShader = Compile(ScaledCopyPs, "ps_5_0", "copy-ps"),
            BlendState = new BlendDescription(Blend.One, Blend.Zero),
            SampleMask = uint.MaxValue,
            RasterizerState = new RasterizerDescription(CullMode.None, FillMode.Solid, false, 0, 0, 0, false, false, false, 0, ConservativeRasterizationMode.Off),
            DepthStencilState = new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always),
            InputLayout = new InputLayoutDescription([]),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = [Format.R8G8B8A8_UNorm],
            DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
        });
    }

    private readonly ID3D12PipelineState _scaledCopy;

    private const string ScaledCopyVs = """
        struct O { float4 p : SV_Position; float2 uv : TEXCOORD0; };
        O main(uint id : SV_VertexID)
        {
            O o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.p = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            o.uv = uv;
            return o;
        }
        """;

    private const string ScaledCopyPs = """
        Texture2D T : register(t0);
        SamplerState S : register(s0);
        float4 main(float4 p : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return T.SampleLevel(S, uv, 0); }
        """;

    public string AdapterName { get; }

    /// <summary>Host pixels per guest pixel along each axis (1 = native 640x480, 6 = 3840x2880).</summary>
    public int Scale { get; }

    /// <summary>Why the backend stopped, or null while it works.</summary>
    public string? FailureReason { get; private set; }

    public long Draws { get; private set; }
    public long Triangles { get; private set; }
    public long FramesPresented { get; private set; }
    public int ShaderCount => _pixelShaders.Count;
    public int PipelineCount => _pipelines.Count;

    /// <summary>The adapter can apply logic operations (needed for NV2A SET_LOGIC_OP draws).</summary>
    public bool LogicOpSupported => _logicOpSupported;

    /// <summary>Creates the backend on the high-performance adapter (or WARP, the software Direct3D 12, when asked or as a fallback).</summary>
    /// <param name="ownsMemory">
    /// True: the GPU does the rendering and the CPU rasteriser is skipped (fast); guest memory is kept coherent on
    /// access. False: the GPU only mirrors the CPU renderer's work at a higher resolution.
    /// </param>
    public static D3D12Backend? TryCreate(int scale, bool useWarp, bool ownsMemory, out string? error)
    {
        error = null;
        scale = Math.Clamp(scale, 1, 8);
        try
        {
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory6>(false);
            if (!useWarp)
            {
                for (uint i = 0; factory.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).Success && adapter is not null; i++)
                {
                    using (adapter)
                    {
                        var desc = adapter.Description1;
                        if ((desc.Flags & AdapterFlags.Software) != 0) continue;
                        if (Vortice.Direct3D12.D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? device).Success && device is not null)
                            return new D3D12Backend(device, desc.Description, scale, ownsMemory);
                    }
                }
            }
            using var warp = factory.EnumWarpAdapter<IDXGIAdapter1>();
            if (Vortice.Direct3D12.D3D12.D3D12CreateDevice(warp, FeatureLevel.Level_11_0, out ID3D12Device? warpDevice).Success && warpDevice is not null)
                return new D3D12Backend(warpDevice, "Microsoft Basic Render Driver (WARP, software)", scale, ownsMemory);
            error = "No Direct3D 12 device could be created.";
            return null;
        }
#pragma warning disable CA1031 // Any failure to bring up Direct3D 12 leaves the CPU renderer in charge; the reason is reported.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            error = $"Direct3D 12 is unavailable: {ex.Message}";
            return null;
        }
    }

    /// <summary>Connects the backend to a machine's NV2A (texture data is read from its memory).</summary>
    public void Attach(Nv2aDevice gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        _memory = gpu.Memory;
        _gpu = gpu;
        if (OwnsMemory) gpu.Memory.WatchRam(0, this); // main RAM (physical 0) and all its mirrors
        gpu.Backend = this;
    }

    private static ReadOnlyMemory<byte> Compile(string source, string profile, string name) =>
        Compiler.Compile(source, "main", name, profile, ShaderFlags.OptimizationLevel3, EffectFlags.None);

    // ------------------------------------------------------------------ IRenderBackend

    /// <summary>
    /// True: this backend owns render-target memory (the CPU rasteriser is off). Each colour target is then in one of
    /// three states: the GPU copy is newer (guest pages watched for any access), both agree (watched for writes), or
    /// guest memory is newer (uploaded to the GPU before its next use).
    /// </summary>
    public bool OwnsMemory => _ownsMemory && FailureReason is null;

    private readonly bool _ownsMemory;

    public long Downloads { get; private set; }
    public long Uploads { get; private set; }
    public long GpuBlits { get; private set; }
    public long TargetTextures { get; private set; }

    public void Clear(GpuClear clear)
    {
        ArgumentNullException.ThrowIfNull(clear);
        Guard(() =>
        {
            BeginList();
            var s = Scale;
            if ((clear.Flags & 0xF0) != 0 && clear.Color.Present)
            {
                var target = ColorTarget(clear.Color, clear.Width, clear.Height, clear.AaX, clear.AaY, clear.X1 + 1, clear.Y1 + 1);
                Prepare(target);
                var full = clear.Color.Bpp != 4 || (clear.Flags & 0x70) == 0x70;
                if (!full)
                {
                    // per-channel clears cannot be expressed as a host clear
                    if (OwnsMemory) ReportOnce("Direct3D 12: per-channel colour clears are not supported - cleared as a whole");
                    else { target.Stale = true; return; }
                }
                Transition(target, ResourceStates.RenderTarget);
                var rect = new RawRect(clear.X0 * s, clear.Y0 * s, (clear.X1 + 1) * s, (clear.Y1 + 1) * s);
                _list.ClearRenderTargetView(Rtv(target), new Color4(DecodeColor(clear.Color.Format, clear.ColorValue)), [rect]);
                target.LastWriteVBlank = CurrentVBlank;
                target.Stale = false;
                Dirtied(target);
            }
            if ((clear.Flags & 3) != 0 && clear.Zeta.Present)
            {
                var depth = DepthTarget(clear.Zeta, Math.Max(clear.Width * clear.AaX, clear.X1 + 1), Math.Max(clear.Height * clear.AaY, clear.Y1 + 1));
                Prepare(depth);
                Transition(depth, ResourceStates.DepthWrite);
                var flags = (ClearFlags)0;
                if ((clear.Flags & 1) != 0) flags |= ClearFlags.Depth;
                if ((clear.Flags & 2) != 0 && clear.Zeta.Bpp == 4) flags |= ClearFlags.Stencil;
                if (flags == 0) return;
                float z = clear.Zeta.Bpp == 4 ? (clear.ZetaValue >> 8) / 16777215f : (clear.ZetaValue & 0xFFFF) / 65535f;
                var rect = new RawRect(clear.X0 * s, clear.Y0 * s, (clear.X1 + 1) * s, (clear.Y1 + 1) * s);
                _list.ClearDepthStencilView(Dsv(depth), flags, z, (byte)(clear.ZetaValue & 0xFF), [rect]);
                Dirtied(depth);
            }
        });
    }

    public void Draw(GpuDraw draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        if (!draw.Color.Present || draw.VertexCount == 0) return;
        Guard(() =>
        {
            BeginList();
            // Everything that may read guest memory or flush (textures, uploads, write-backs) happens first, so the
            // command list and descriptor rings are not reset in the middle of recording this draw.
            var color = ColorTarget(draw.Color, draw.Width, draw.Height, draw.AaX, draw.AaY, draw.ClipX1, draw.ClipY1);
            Prepare(color);
            Target? depth = draw.Zeta.Present && (draw.DepthTest || draw.DepthWrite || draw.StencilActive)
                ? DepthTarget(draw.Zeta, color.GuestWidth * draw.AaX, color.GuestHeight * draw.AaY)
                : null;
            if (depth is not null) Prepare(depth);
            var sources = new (ID3D12Resource? Resource, Target? Target)[4];
            var texUv = new float[16];
            for (var t = 0; t < 4; t++)
            {
                texUv[(t * 4) + 0] = 1;
                texUv[(t * 4) + 1] = 1;
                var unit = draw.StageModes[t] == 1 ? draw.Textures[t] : null;
                if (unit is null) continue;
                if (OwnsMemory && TargetAt(unit.Address) is { } rt && rt != color && rt.Owner != Owner.Memory)
                {
                    // render to texture: sample the host copy at full resolution
                    sources[t] = (rt.Resource, rt);
                    texUv[(t * 4) + 0] = unit.Width / (float)(rt.GuestWidth * rt.AaX);
                    texUv[(t * 4) + 1] = unit.Height / (float)(rt.GuestHeight * rt.AaY);
                    texUv[(t * 4) + 2] = Textures.Decode(unit.Format, 0).W == 1 ? 1 : 0; // formats without alpha read alpha 1
                    TargetTextures++;
                }
                else sources[t] = (Texture(unit), null);
            }

            var bytes = (long)draw.VertexCount * GpuDraw.FloatsPerVertex * 4;
            if (_uploadOffset + bytes + CbBytes + 512 > UploadBytes || _srvNext + 4 > SrvRing || _samplerNext + 4 > SamplerRing) Flush();
            BeginList();

            foreach (var (_, rt) in sources) if (rt is not null) Transition(rt, ResourceStates.PixelShaderResource);
            Transition(color, ResourceStates.RenderTarget);
            if (depth is not null) Transition(depth, ResourceStates.DepthWrite);

            // textures and samplers (descriptor tables of 4)
            var srvBase = _srvNext;
            var samplerBase = _samplerNext;
            _srvNext += 4;
            _samplerNext += 4;
            for (var t = 0; t < 4; t++)
            {
                var srvHandle = _srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(srvBase + t, _srvInc);
                var samplerHandle = _samplerHeap.GetCPUDescriptorHandleForHeapStart().Offset(samplerBase + t, _samplerInc);
                var unit = draw.StageModes[t] == 1 ? draw.Textures[t] : null;
                _device.CreateShaderResourceView(sources[t].Resource!, TextureView, srvHandle);
                var sampler = Sampler(unit);
                _device.CreateSampler(ref sampler, samplerHandle);
            }

            // pipeline
            var key = PixelShaderKey.From(draw);
            if (!_pixelShaders.TryGetValue(key, out var ps))
            {
                ps = Compile(ShaderGenerator.PixelShader(draw), "ps_5_0", "nv2a-ps");
                _pixelShaders[key] = ps;
            }
            var logic = draw.Ops.LogicOp && _logicOpSupported;
            _list.SetPipelineState(Pipeline(key, ps, draw, depth, logic));

            // constants and vertices
            var zMax = draw.Zeta.Bpp == 2 ? 65535.0 : 16777215.0;
            var constants = ShaderGenerator.Constants(draw, Scale, color.HostWidth, color.HostHeight, zMax, texUv);
            var cbOffset = Align(_uploadOffset, 256);
            fixed (float* src = constants) Buffer.MemoryCopy(src, _uploadPtr + cbOffset, CbBytes, constants.Length * 4);
            var vbOffset = Align(cbOffset + CbBytes, 16);
            fixed (float* src = draw.Vertices) Buffer.MemoryCopy(src, _uploadPtr + vbOffset, bytes, bytes);
            _uploadOffset = vbOffset + bytes;

            _list.SetGraphicsRootSignature(_root);
            _list.SetDescriptorHeaps([_srvHeap, _samplerHeap]);
            _list.SetGraphicsRootConstantBufferView(0, _upload.GPUVirtualAddress + (ulong)cbOffset);
            _list.SetGraphicsRootDescriptorTable(1, _srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(srvBase, _srvInc));
            _list.SetGraphicsRootDescriptorTable(2, _samplerHeap.GetGPUDescriptorHandleForHeapStart().Offset(samplerBase, _samplerInc));
            _list.OMSetRenderTargets(logic ? RtvUInt(color) : Rtv(color), depth is null ? null : Dsv(depth));
            _list.RSSetViewport(new Viewport(0, 0, color.HostWidth, color.HostHeight, 0, 1));
            _list.RSSetScissorRect(new RawRect(draw.ClipX0 * Scale, draw.ClipY0 * Scale, draw.ClipX1 * Scale, draw.ClipY1 * Scale));
            var k = draw.Ops.Constant;
            _list.OMSetBlendFactor(new Color4(k.X, k.Y, k.Z, k.W));
            _list.OMSetStencilRef(draw.Ops.StencilRef & draw.Ops.StencilFuncMask);
            _list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _list.IASetVertexBuffers(0, new VertexBufferView(_upload.GPUVirtualAddress + (ulong)vbOffset, (uint)bytes, GpuDraw.FloatsPerVertex * 4));
            _list.DrawInstanced((uint)draw.VertexCount, 1, 0, 0);

            color.LastWriteVBlank = CurrentVBlank;
            if (draw.Ops.LogicOp && !_logicOpSupported)
            {
                if (OwnsMemory) ReportOnce("Direct3D 12: this adapter cannot apply logic operations - drawn without them");
                else color.Stale = true; // the host image of this target is wrong from here on: show the CPU picture
            }
            else color.Stale = false;
            Dirtied(color);
            if (depth is not null && (draw.DepthWrite || (draw.StencilActive && draw.Ops.StencilWrite))) Dirtied(depth);
            Draws++;
            Triangles += draw.VertexCount / 3;
        });
    }

    private static readonly ShaderResourceViewDescription TextureView = new()
    {
        Format = Format.R8G8B8A8_UNorm,
        ViewDimension = SrvDimension.Texture2D,
        Shader4ComponentMapping = 0x1688, // default 4-component mapping
        Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
    };

    public void MemoryWritten(uint address, uint length)
    {
        foreach (var t in _depthTargets.Values)
            if (OwnsMemory && address < t.Address + t.Bytes && address + length > t.Address) SetOwner(t, Owner.Memory);
        foreach (var t in _colorTargets.Values)
        {
            if (address >= t.Address + t.Bytes || address + length <= t.Address) continue;
            if (OwnsMemory) SetOwner(t, Owner.Memory); // the 2D engine or a memory copy wrote newer pixels
            else t.Stale = true;                       // mirror mode: show the CPU picture until the target is drawn again
        }
    }

    public bool Blit(GpuBlit blit)
    {
        ArgumentNullException.ThrowIfNull(blit);
        if (!OwnsMemory || FailureReason is not null) return false;
        var done = false;
        Guard(() =>
        {
            var src = TargetAt(blit.SrcAddress, blit.SrcPitch);
            var dstExisting = TargetAt(blit.DstAddress, blit.DstPitch);
            var sameOverlap = src is not null && src == dstExisting
                && blit.InX < blit.OutX + blit.Width && blit.OutX < blit.InX + blit.Width && blit.InY < blit.OutY + blit.Height && blit.OutY < blit.InY + blit.Height;
            if (src is null || src.Owner == Owner.Memory || src.Bpp != blit.Bpp || sameOverlap)
            {
                // the 2D engine copies guest memory: make sure the destination's guest pixels are current first
                // (the source is read through the bus, which writes GPU-held images back on access)
                if (dstExisting is { Owner: Owner.Gpu } d) Download(d);
                return;
            }
            BeginList();
            var dstSurface = new GpuSurface(blit.DstAddress, blit.DstPitch, src.GuestFormat, src.Bpp, true, false, 0, 0);
            var dst = ColorTarget(dstSurface, blit.OutX + blit.Width, blit.OutY + blit.Height, 1, 1, blit.OutX + blit.Width, blit.OutY + blit.Height);
            Prepare(dst);
            Prepare(src);
            var s = Scale;
            int w = Math.Min(blit.Width * s, src.HostWidth - (blit.InX * s)), h = Math.Min(blit.Height * s, src.HostHeight - (blit.InY * s));
            w = Math.Min(w, dst.HostWidth - (blit.OutX * s));
            h = Math.Min(h, dst.HostHeight - (blit.OutY * s));
            if (w <= 0 || h <= 0) { done = true; return; }
            Transition(src, ResourceStates.CopySource);
            Transition(dst, ResourceStates.CopyDest);
            _list.CopyTextureRegion(new TextureCopyLocation(dst.Resource, 0), (uint)(blit.OutX * s), (uint)(blit.OutY * s), 0,
                new TextureCopyLocation(src.Resource, 0), new Box(blit.InX * s, blit.InY * s, 0, (blit.InX * s) + w, (blit.InY * s) + h, 1));
            dst.LastWriteVBlank = CurrentVBlank;
            Dirtied(dst);
            GpuBlits++;
            done = true;
        });
        return done;
    }

    public Frame? TryScanOut(uint address, int width, int height, int pitch, long vblank)
    {
        if (FailureReason is not null || !_colorTargets.TryGetValue((address, pitch), out var target)) { FlushIfOpen(); return null; }
        if (target.AaX != 1 || target.AaY != 1) { FlushIfOpen(); return null; }
        if (!OwnsMemory && (target.Stale || target.LastWriteVBlank < 0 || vblank - target.LastWriteVBlank > 120)) { FlushIfOpen(); return null; }
        if (OwnsMemory && target.Owner == Owner.Memory && target.LastWriteVBlank < 0) { FlushIfOpen(); return null; } // never drawn by the GPU
        Frame? frame = null;
        Guard(() =>
        {
            Prepare(target);
            var w = Math.Min(width * Scale, target.HostWidth);
            var h = Math.Min(height * Scale, target.HostHeight);
            var rgba = ReadTarget(target, w, h);
            var pixels = new uint[w * h];
            for (var i = 0; i < pixels.Length; i++)
            {
                var c = rgba[i]; // R8G8B8A8 in memory = 0xAABBGGRR; frames are 0xAARRGGBB
                pixels[i] = 0xFF00_0000u | ((c & 0xFF) << 16) | (c & 0xFF00) | ((c >> 16) & 0xFF);
            }
            FramesPresented++;
            frame = new Frame(w, h, pixels, vblank);
        });
        return frame;
    }

    // ------------------------------------------------------------------ guest memory coherence

    private enum Owner { Memory, Both, Gpu }

    /// <summary>The bus reports a CPU or device access to a page that holds a GPU-owned (or GPU-mirrored) image.</summary>
    public void OnWatchedAccess(uint ramOffset, int size, bool write)
    {
        Guard(() =>
        {
            foreach (var t in _colorTargets.Values.Concat(_depthTargets.Values).ToList())
            {
                if (!t.Watched || ramOffset >= t.RamOffset + t.Bytes + PageSlack || ramOffset + (uint)size + PageSlack <= t.RamOffset) continue;
                if (t.Owner == Owner.Gpu) Download(t);
                if (write) SetOwner(t, Owner.Memory);
            }
        });
    }

    /// <summary>Watch granularity is 4 KB: an access within the same page as a target counts as touching it.</summary>
    private const uint PageSlack = 1u << PhysicalBus.WatchShift;

    private void SetOwner(Target t, Owner owner)
    {
        t.Owner = owner;
        if (!OwnsMemory || _memory is null || !t.Watched) return;
        _memory.SetWatch(t.RamOffset, t.Bytes, owner switch
        {
            Owner.Gpu => PhysicalBus.WatchAll,
            Owner.Both => PhysicalBus.WatchWrites,
            _ => PhysicalBus.WatchNone,
        });
    }

    /// <summary>The GPU wrote this target: guest memory is now stale.</summary>
    private void Dirtied(Target t)
    {
        if (OwnsMemory) SetOwner(t, Owner.Gpu);
    }

    /// <summary>Before the GPU uses a target: when guest memory holds newer pixels, upload them.</summary>
    private void Prepare(Target t)
    {
        if (OwnsMemory && t.Owner == Owner.Memory) Upload(t);
    }

    /// <summary>Guest layout offset of sample (x, y) of a target.</summary>
    private static uint GuestOffset(Target t, int x, int y) => t.Swizzled
        ? Textures.SwizzleOffset(x, y, t.SwizzleWidth, t.SwizzleHeight) * (uint)t.Bpp
        : (uint)((y * t.Pitch) + (x * t.Bpp));

    /// <summary>Copies guest memory into the host target (scaled up, nearest), then both agree.</summary>
    private void Upload(Target t)
    {
        if (_memory is null) return;
        if (t.Depth) { UploadDepth(t); return; }
        int sw = t.GuestWidth * t.AaX, sh = t.GuestHeight * t.AaY;
        var rowSamples = t.Swizzled ? sw : Math.Min(sw, t.Pitch / t.Bpp);
        var pixels = new uint[sw * sh];
        if (_memory.TryGetWritableRam(t.Address, (int)t.Bytes, out var ram))
        {
            for (var y = 0; y < sh; y++)
                for (var x = 0; x < rowSamples; x++)
                {
                    var o = (int)GuestOffset(t, x, y);
                    if (o + t.Bpp > ram.Length) continue;
                    uint v = t.Bpp switch { 4 => BitConverter.ToUInt32(ram.Slice(o, 4)), 2 => BitConverter.ToUInt16(ram.Slice(o, 2)), _ => ram[o] };
                    var c = t.Bpp == 1 ? new System.Numerics.Vector4(v / 255f, v / 255f, v / 255f, 1) : DecodeColor(t.GuestFormat, v);
                    pixels[(y * sw) + x] = (B(c.W) << 24) | (B(c.Z) << 16) | (B(c.Y) << 8) | B(c.X);
                }
        }
        BeginList();
        var temp = UploadTexture(pixels, sw, sh, Format.R8G8B8A8_Typeless);
        Transition(t, ResourceStates.RenderTarget);
        DrawScaled(temp, t);
        _pendingRelease.Add(temp);
        Uploads++;
        Log($"upload 0x{t.Address:X8} aa {t.AaX}x{t.AaY}");
        SetOwner(t, Owner.Both);
    }

    /// <summary>Writes the host target back to guest memory in the guest format (one host pixel per guest sample).</summary>
    private void Download(Target t)
    {
        if (_memory is null) return;
        if (t.Depth) { DownloadDepth(t); return; }
        var rgba = ReadTarget(t, t.HostWidth, t.HostHeight);
        int sw = t.GuestWidth * t.AaX, sh = t.GuestHeight * t.AaY, s = Scale;
        var rowSamples = t.Swizzled ? sw : Math.Min(sw, t.Pitch / t.Bpp);
        if (_memory.TryGetWritableRam(t.Address, (int)t.Bytes, out var ram))
        {
            for (var y = 0; y < sh; y++)
                for (var x = 0; x < rowSamples; x++)
                {
                    var c = rgba[(((y * s) + (s / 2)) * t.HostWidth) + (x * s) + (s / 2)];
                    var o = (int)GuestOffset(t, x, y);
                    if (o + t.Bpp > ram.Length) continue;
                    var v = Encode(t, c);
                    switch (t.Bpp)
                    {
                        case 4: BitConverter.TryWriteBytes(ram.Slice(o, 4), v); break;
                        case 2: BitConverter.TryWriteBytes(ram.Slice(o, 2), (ushort)v); break;
                        default: ram[o] = (byte)v; break;
                    }
                }
        }
        Downloads++;
        Log($"download 0x{t.Address:X8} aa {t.AaX}x{t.AaY}");
        SetOwner(t, Owner.Both);
    }

    /// <summary>Host RGBA8 (0xAABBGGRR) to the guest surface format, as <c>KelvinRenderer.EncodeColor</c> stores it.</summary>
    private static uint Encode(Target t, uint c)
    {
        uint r = c & 0xFF, g = (c >> 8) & 0xFF, b = (c >> 16) & 0xFF, a = c >> 24;
        if (t.Bpp == 4)
        {
            var alpha = t.GuestFormat switch { 4 => 0u, 5 => 0xFFu, 6 or 7 => a & 0x7F, _ => a };
            return (alpha << 24) | (r << 16) | (g << 8) | b;
        }
        if (t.Bpp == 1) return r;
        if (t.GuestFormat == 3) return ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
        return ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3) | (t.GuestFormat == 2 ? 0x8000u : 0);
    }

    private static uint B(float v) => (uint)Math.Clamp(MathF.Round(v * 255), 0, 255);

    /// <summary>The colour target whose guest memory starts at <paramref name="address"/> (any pitch, or the given one).</summary>
    private Target? TargetAt(uint address, int pitch = -1)
    {
        if (pitch >= 0) return _colorTargets.GetValueOrDefault((address, pitch));
        foreach (var ((a, _), t) in _colorTargets) if (a == address) return t;
        return null;
    }

    // ------------------------------------------------------------------ targets

    private Target ColorTarget(GpuSurface s, int width, int height, int aaX, int aaY, int needX, int needY)
    {
        var guestW = Math.Max(s.Swizzled ? s.SwizzleWidth : Math.Max(1, s.Pitch / Math.Max(1, s.Bpp)) / aaX, width);
        var guestH = Math.Max(Math.Max(height, 1), (needY + aaY - 1) / aaY);
        guestW = Math.Max(guestW, (needX + aaX - 1) / aaX);
        if (s.Swizzled) { guestW = s.SwizzleWidth / aaX; guestH = Math.Max(1, s.SwizzleHeight / aaY); }
        if (_colorTargets.TryGetValue((s.Address, s.Pitch), out var t) && t.GuestWidth >= guestW && t.GuestHeight >= guestH && t.AaX == aaX && t.AaY == aaY) return t;
        if (t is not null && t.AaX == aaX && t.AaY == aaY)
        {
            // only sizes in the same units (pixels of the same anti-aliasing) carry over
            guestH = Math.Max(guestH, t.GuestHeight);
            guestW = Math.Max(guestW, t.GuestWidth);
        }
        if (t is not null)
        {
            // the target grows: its pixels go through guest memory into the new one
            if (OwnsMemory && t.Owner == Owner.Gpu) Download(t);
            SetOwner(t, Owner.Memory);
            Retire(t);
        }
        var created = CreateTarget(guestW, guestH, aaX, aaY, depth: false, Format.R8G8B8A8_Typeless);
        Log($"create 0x{s.Address:X8} pitch {s.Pitch} {guestW}x{guestH} aa {aaX}x{aaY}{(t is null ? "" : " (replaces)")}");
        created.Address = s.Address;
        created.Pitch = s.Pitch;
        created.Bpp = Math.Max(1, s.Bpp);
        created.GuestFormat = s.Format;
        created.Swizzled = s.Swizzled;
        created.SwizzleWidth = s.SwizzleWidth;
        created.SwizzleHeight = s.SwizzleHeight;
        created.Bytes = s.Swizzled ? (uint)(s.SwizzleWidth * s.SwizzleHeight * created.Bpp) : (uint)(s.Pitch * guestH * aaY);
        if (_memory is not null && _memory.TryWatchedOffset(s.Address, out var offset))
        {
            created.RamOffset = offset;
            created.Watched = OwnsMemory;
        }
        created.Owner = Owner.Memory; // guest memory is the truth until the GPU first draws: uploaded on first use
        _colorTargets[(s.Address, s.Pitch)] = created;
        return created;
    }

    private Target DepthTarget(GpuSurface s, int widthSamples, int heightSamples)
    {
        var format = s.Bpp == 2 ? Format.D16_UNorm : Format.D24_UNorm_S8_UInt;
        if (s.Swizzled) { widthSamples = s.SwizzleWidth; heightSamples = s.SwizzleHeight; }
        if (_depthTargets.TryGetValue(s.Address, out var t) && t.GuestWidth >= widthSamples && t.GuestHeight >= heightSamples && t.Format == format) return t;
        if (t is not null)
        {
            if (t.Format == format)
            {
                widthSamples = Math.Max(widthSamples, t.GuestWidth);
                heightSamples = Math.Max(heightSamples, t.GuestHeight);
            }
            if (OwnsMemory && t.Owner == Owner.Gpu) Download(t); // its contents go through guest memory
            SetOwner(t, Owner.Memory);
            Retire(t);
        }
        var created = CreateTarget(widthSamples, heightSamples, 1, 1, depth: true, format);
        created.Address = s.Address;
        created.Pitch = s.Pitch;
        created.Bpp = s.Bpp == 2 ? 2 : 4;
        created.GuestFormat = s.Format;
        created.Swizzled = s.Swizzled;
        created.SwizzleWidth = s.SwizzleWidth;
        created.SwizzleHeight = s.SwizzleHeight;
        created.Bytes = s.Swizzled ? (uint)(s.SwizzleWidth * s.SwizzleHeight * created.Bpp) : (uint)(s.Pitch * heightSamples);
        if (_memory is not null && _memory.TryWatchedOffset(s.Address, out var offset))
        {
            created.RamOffset = offset;
            created.Watched = OwnsMemory;
        }
        created.Owner = Owner.Memory;
        _depthTargets[s.Address] = created;
        return created;
    }

    /// <summary>Footprints of the depth (and stencil) planes of a depth target.</summary>
    private (PlacedSubresourceFootPrint[] Layouts, ulong Total) DepthFootprints(Target t, ulong baseOffset)
    {
        var planes = t.Format == Format.D24_UNorm_S8_UInt ? 2u : 1u;
        var layouts = new PlacedSubresourceFootPrint[planes];
        var rows = new uint[planes];
        var rowSizes = new ulong[planes];
        _device.GetCopyableFootprints(t.Resource.Description, 0, planes, baseOffset, layouts, rows, rowSizes, out var total);
        return (layouts, total);
    }

    /// <summary>Depth buffer to guest memory: one host pixel per guest sample, Z24S8 or Z16 words.</summary>
    private void DownloadDepth(Target t)
    {
        BeginList();
        var (layouts, total) = DepthFootprints(t, 0);
        var buffer = _device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(total, ResourceFlags.None, 0), ResourceStates.CopyDest, null);
        try
        {
            var before = t.State;
            Transition(t, ResourceStates.CopySource);
            for (var p = 0; p < layouts.Length; p++)
                _list.CopyTextureRegion(new TextureCopyLocation(buffer, layouts[p]), 0, 0, 0, new TextureCopyLocation(t.Resource, (uint)p), null);
            Transition(t, before == ResourceStates.CopySource ? ResourceStates.DepthWrite : before);
            Flush();
            void* mapped;
            buffer.Map(0, &mapped).CheckError();
            var data = (byte*)mapped;
            int s = Scale, sw = t.GuestWidth, sh = t.GuestHeight;
            var rowSamples = t.Swizzled ? sw : Math.Min(sw, t.Pitch / t.Bpp);
            if (_memory!.TryGetWritableRam(t.Address, (int)t.Bytes, out var ram))
            {
                for (var y = 0; y < sh; y++)
                    for (var x = 0; x < rowSamples; x++)
                    {
                        int hx = (x * s) + (s / 2), hy = (y * s) + (s / 2);
                        var o = (int)GuestOffset(t, x, y);
                        if (o + t.Bpp > ram.Length) continue;
                        if (t.Bpp == 2)
                        {
                            var d16 = *(ushort*)(data + (long)layouts[0].Offset + (hy * (long)layouts[0].Footprint.RowPitch) + (hx * 2));
                            BitConverter.TryWriteBytes(ram.Slice(o, 2), d16);
                        }
                        else
                        {
                            var d24 = *(uint*)(data + (long)layouts[0].Offset + (hy * (long)layouts[0].Footprint.RowPitch) + (hx * 4)) & 0xFF_FFFF;
                            var st = data[(long)layouts[1].Offset + (hy * (long)layouts[1].Footprint.RowPitch) + hx];
                            BitConverter.TryWriteBytes(ram.Slice(o, 4), (d24 << 8) | st);
                        }
                    }
            }
            buffer.Unmap(0, null);
        }
        finally
        {
            buffer.Dispose();
        }
        Downloads++;
        Log($"download depth 0x{t.Address:X8}");
        SetOwner(t, Owner.Both);
    }

    /// <summary>Guest depth words to the depth buffer (each guest sample fills its scale x scale host block).</summary>
    private void UploadDepth(Target t)
    {
        BeginList();
        var (probe, total) = DepthFootprints(t, 0);
        var offset = Align(_uploadOffset, 512);
        if (offset + (long)total > UploadBytes) { Flush(); BeginList(); offset = 0; }
        var (layouts, _) = DepthFootprints(t, (ulong)offset);
        _ = probe;
        int s = Scale, sw = t.GuestWidth, sh = t.GuestHeight;
        var rowSamples = t.Swizzled ? sw : Math.Min(sw, t.Pitch / t.Bpp);
        if (_memory!.TryGetWritableRam(t.Address, (int)t.Bytes, out var ram))
        {
            for (var hy = 0; hy < t.HostHeight; hy++)
            {
                var y = hy / s;
                for (var hx = 0; hx < t.HostWidth; hx++)
                {
                    var x = hx / s;
                    uint word = 0;
                    if (y < sh && x < rowSamples)
                    {
                        var o = (int)GuestOffset(t, x, y);
                        if (o + t.Bpp <= ram.Length) word = t.Bpp == 2 ? BitConverter.ToUInt16(ram.Slice(o, 2)) : BitConverter.ToUInt32(ram.Slice(o, 4));
                    }
                    if (t.Bpp == 2) *(ushort*)(_uploadPtr + (long)layouts[0].Offset + (hy * (long)layouts[0].Footprint.RowPitch) + (hx * 2)) = (ushort)word;
                    else
                    {
                        *(uint*)(_uploadPtr + (long)layouts[0].Offset + (hy * (long)layouts[0].Footprint.RowPitch) + (hx * 4)) = word >> 8;
                        _uploadPtr[(long)layouts[1].Offset + (hy * (long)layouts[1].Footprint.RowPitch) + hx] = (byte)word;
                    }
                }
            }
        }
        _uploadOffset = offset + (long)total;
        Transition(t, ResourceStates.CopyDest);
        for (var p = 0; p < layouts.Length; p++)
            _list.CopyTextureRegion(new TextureCopyLocation(t.Resource, (uint)p), 0, 0, 0, new TextureCopyLocation(_upload, layouts[p]), null);
        Transition(t, ResourceStates.DepthWrite);
        Uploads++;
        Log($"upload depth 0x{t.Address:X8}");
        SetOwner(t, Owner.Both);
    }

    private Target CreateTarget(int guestW, int guestH, int aaX, int aaY, bool depth, Format format)
    {
        var w = guestW * aaX * Scale;
        var h = guestH * aaY * Scale;
        var flags = depth ? ResourceFlags.AllowDepthStencil : ResourceFlags.AllowRenderTarget;
        var state = depth ? ResourceStates.DepthWrite : ResourceStates.RenderTarget;
        ClearValue? clear = depth ? new ClearValue(format, 1, 0) : null; // typeless colour targets take no optimised clear value
        var resource = _device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(format, (uint)w, (uint)h, 1, 1, 1, 0, flags, TextureLayout.Unknown, 0), state, clear);
        var handle = depth ? _freeDsv.Dequeue() : _freeRtv.Dequeue();
        if (depth) _device.CreateDepthStencilView(resource, null, _dsvHeap.GetCPUDescriptorHandleForHeapStart().Offset(handle, _dsvInc));
        else
        {
            _device.CreateRenderTargetView(resource, new RenderTargetViewDescription { Format = Format.R8G8B8A8_UNorm, ViewDimension = RenderTargetViewDimension.Texture2D },
                _rtvHeap.GetCPUDescriptorHandleForHeapStart().Offset(handle, _rtvInc));
            _device.CreateRenderTargetView(resource, new RenderTargetViewDescription { Format = Format.R8G8B8A8_UInt, ViewDimension = RenderTargetViewDimension.Texture2D },
                _rtvHeap.GetCPUDescriptorHandleForHeapStart().Offset(handle + 256, _rtvInc));
        }
        return new Target
        {
            Resource = resource, Handle = handle, GuestWidth = guestW, GuestHeight = guestH, AaX = aaX, AaY = aaY,
            Depth = depth, Format = format, State = state, HostWidth = w, HostHeight = h,
        };
    }

    private void Retire(Target t)
    {
        _pendingRelease.Add(t.Resource);
        if (t.Depth) _freeDsv.Enqueue(t.Handle); else _freeRtv.Enqueue(t.Handle);
    }

    private CpuDescriptorHandle Rtv(Target t) => _rtvHeap.GetCPUDescriptorHandleForHeapStart().Offset(t.Handle, _rtvInc);

    private CpuDescriptorHandle RtvUInt(Target t) => _rtvHeap.GetCPUDescriptorHandleForHeapStart().Offset(t.Handle + 256, _rtvInc);

    private CpuDescriptorHandle Dsv(Target t) => _dsvHeap.GetCPUDescriptorHandleForHeapStart().Offset(t.Handle, _dsvInc);

    private void Transition(Target t, ResourceStates to)
    {
        if (t.State == to) return;
        _list.ResourceBarrierTransition(t.Resource, t.State, to, uint.MaxValue, ResourceBarrierFlags.None);
        t.State = to;
    }

    /// <summary>Decodes a guest render-target value to RGBA, as <c>KelvinRenderer.DecodeColor</c> does (alpha 1 without an alpha channel).</summary>
    public static System.Numerics.Vector4 DecodeColor(uint format, uint v) => format switch
    {
        3 => new(((v >> 11) & 31) / 31f, ((v >> 5) & 63) / 63f, (v & 31) / 31f, 1),
        1 or 2 => new(((v >> 10) & 31) / 31f, ((v >> 5) & 31) / 31f, (v & 31) / 31f, 1),
        6 or 7 => new(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, ((v >> 24) & 0x7F) / 127f),
        8 => new(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, (v >> 24) / 255f),
        _ => new(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, 1),
    };

    // ------------------------------------------------------------------ textures

    private static int RawBytes(TextureUnit t)
    {
        if (t.Compressed) return Math.Max(1, (t.Width + 3) / 4) * Math.Max(1, (t.Height + 3) / 4) * (t.Format == 0x0C ? 8 : 16);
        var bpp = Textures.BytesPerTexel(t.Format);
        return t.Swizzled ? t.Width * t.Height * bpp : t.Pitch * t.Height;
    }

    private static ulong Hash(ReadOnlySpan<byte> data)
    {
        var h = 14695981039346656037UL;
        var words = MemoryMarshal.Cast<byte, ulong>(data);
        foreach (var w in words) h = (h ^ w) * 1099511628211UL;
        for (var i = words.Length * 8; i < data.Length; i++) h = (h ^ data[i]) * 1099511628211UL;
        return h;
    }

    private ID3D12Resource? Texture(TextureUnit unit)
    {
        if (_memory is null || unit.Width <= 0 || unit.Height <= 0 || unit.Width > 4096 || unit.Height > 4096) return null;
        var raw = _memory.ReadBytes(unit.Address, RawBytes(unit));
        var hash = Hash(raw);
        var key = (unit.Address, unit.Format, unit.Width, unit.Height, unit.Pitch);
        if (_textures.TryGetValue(key, out var cached) && cached.Hash == hash)
        {
            cached.LastUse = Draws;
            return cached.Resource;
        }

        // decode texel by texel through the CPU renderer's own decoder, so both renderers read the same colours
        var pixels = new uint[unit.Width * unit.Height];
        for (var y = 0; y < unit.Height; y++)
            for (var x = 0; x < unit.Width; x++)
            {
                var c = Textures.Texel(_memory, unit, x, y);
                pixels[(y * unit.Width) + x] = (B(c.W) << 24) | (B(c.Z) << 16) | (B(c.Y) << 8) | B(c.X);
            }
        var resource = UploadTexture(pixels, unit.Width, unit.Height, Format.R8G8B8A8_UNorm);
        if (cached is not null) _pendingRelease.Add(cached.Resource);
        _textures[key] = new CachedTexture { Resource = resource, Hash = hash, LastUse = Draws };
        return resource;
    }

    /// <summary>Creates a texture from RGBA8 pixels (0xAABBGGRR) and records its upload; it ends in the shader-resource state.</summary>
    private ID3D12Resource UploadTexture(uint[] pixels, int width, int height, Format format)
    {
        var desc = ResourceDescription.Texture2D(format, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.None, TextureLayout.Unknown, 0);
        var resource = _device.CreateCommittedResource(HeapType.Default, HeapFlags.None, desc, ResourceStates.CopyDest, null);
        var layouts = new PlacedSubresourceFootPrint[1];
        var rows = new uint[1];
        var rowSizes = new ulong[1];
        _device.GetCopyableFootprints(desc, 0, 1, 0, layouts, rows, rowSizes, out var total);
        var offset = Align(_uploadOffset, 512);
        if (offset + (long)total > UploadBytes) { Flush(); BeginList(); offset = 0; }
        var rowPitch = (int)layouts[0].Footprint.RowPitch;
        fixed (uint* src = pixels)
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(src + (y * width), _uploadPtr + offset + (y * rowPitch), rowPitch, width * 4);
        _uploadOffset = offset + (long)total;
        layouts[0].Offset = (ulong)offset;
        _list.CopyTextureRegion(new TextureCopyLocation(resource, 0), 0, 0, 0, new TextureCopyLocation(_upload, layouts[0]), null);
        _list.ResourceBarrierTransition(resource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource, uint.MaxValue, ResourceBarrierFlags.None);
        return resource;
    }

    /// <summary>Draws <paramref name="source"/> over the whole of <paramref name="target"/> (scaled up, nearest).</summary>
    private void DrawScaled(ID3D12Resource source, Target target)
    {
        if (_srvNext + 1 > SrvRing || _samplerNext + 1 > SamplerRing) { Flush(); BeginList(); }
        var srv = _srvNext++;
        var smp = _samplerNext++;
        _device.CreateShaderResourceView(source, TextureView, _srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(srv, _srvInc));
        var sampler = new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, 0);
        _device.CreateSampler(ref sampler, _samplerHeap.GetCPUDescriptorHandleForHeapStart().Offset(smp, _samplerInc));
        Transition(target, ResourceStates.RenderTarget);
        _list.SetPipelineState(_scaledCopy);
        _list.SetGraphicsRootSignature(_root);
        _list.SetDescriptorHeaps([_srvHeap, _samplerHeap]);
        _list.SetGraphicsRootConstantBufferView(0, _upload.GPUVirtualAddress);
        _list.SetGraphicsRootDescriptorTable(1, _srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(srv, _srvInc));
        _list.SetGraphicsRootDescriptorTable(2, _samplerHeap.GetGPUDescriptorHandleForHeapStart().Offset(smp, _samplerInc));
        _list.OMSetRenderTargets(Rtv(target), null);
        _list.RSSetViewport(new Viewport(0, 0, target.HostWidth, target.HostHeight, 0, 1));
        _list.RSSetScissorRect(new RawRect(0, 0, target.HostWidth, target.HostHeight));
        _list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _list.DrawInstanced(3, 1, 0, 0);
    }

    private static TextureAddressMode Address(int mode) => mode switch
    {
        1 => TextureAddressMode.Wrap,
        2 => TextureAddressMode.Mirror,
        4 => TextureAddressMode.Border,
        _ => TextureAddressMode.Clamp,
    };

    private static SamplerDescription Sampler(TextureUnit? unit)
    {
        if (unit is null) return new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, 0);
        // the CPU renderer uses the magnification filter for all sampling (no derivatives, level 0 only): so does this
        var filter = unit.MagFilter == 2 ? Filter.MinMagLinearMipPoint : Filter.MinMagMipPoint;
        var b = unit.Border;
        return new SamplerDescription(filter, Address(unit.AddressU), Address(unit.AddressV), TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, new Color4(b.X, b.Y, b.Z, b.W), 0, 0);
    }

    // ------------------------------------------------------------------ pipeline state

    private static Blend BlendFactor(uint f, bool alpha) => f switch
    {
        0 => Blend.Zero,
        1 => Blend.One,
        0x300 => alpha ? Blend.SourceAlpha : Blend.SourceColor,
        0x301 => alpha ? Blend.InverseSourceAlpha : Blend.InverseSourceColor,
        0x302 => Blend.SourceAlpha,
        0x303 => Blend.InverseSourceAlpha,
        0x304 => Blend.DestinationAlpha,
        0x305 => Blend.InverseDestinationAlpha,
        0x306 => alpha ? Blend.DestinationAlpha : Blend.DestinationColor,
        0x307 => alpha ? Blend.InverseDestinationAlpha : Blend.InverseDestinationColor,
        0x308 => alpha ? Blend.One : Blend.SourceAlphaSaturate,
        0x8001 or 0x8003 => Blend.BlendFactor,
        0x8002 or 0x8004 => Blend.InverseBlendFactor,
        _ => Blend.One,
    };

    private static BlendOperation BlendOp(uint e) => e switch
    {
        0x800A => BlendOperation.Subtract,
        0x800B => BlendOperation.RevSubtract,
        0x8007 => BlendOperation.Min,
        0x8008 => BlendOperation.Max,
        _ => BlendOperation.Add,
    };

    private static ComparisonFunction Compare(uint f) => f switch
    {
        0x200 => ComparisonFunction.Never,
        0x201 => ComparisonFunction.Less,
        0x202 => ComparisonFunction.Equal,
        0x203 => ComparisonFunction.LessEqual,
        0x204 => ComparisonFunction.Greater,
        0x205 => ComparisonFunction.NotEqual,
        0x206 => ComparisonFunction.GreaterEqual,
        _ => ComparisonFunction.Always,
    };

    private static StencilOperation StencilOp(uint op) => op switch
    {
        0 => StencilOperation.Zero,
        0x1E01 => StencilOperation.Replace,
        0x1E02 => StencilOperation.IncrementSaturate,
        0x1E03 => StencilOperation.DecrementSaturate,
        0x150A => StencilOperation.Invert,
        0x8507 => StencilOperation.Increment,
        0x8508 => StencilOperation.Decrement,
        _ => StencilOperation.Keep,
    };

    private static LogicOp Logic(uint op) => op switch
    {
        0x1500 => LogicOp.Clear, 0x1501 => LogicOp.And, 0x1502 => LogicOp.AndReverse, 0x1503 => LogicOp.Copy,
        0x1504 => LogicOp.AndInverted, 0x1505 => LogicOp.Noop, 0x1506 => LogicOp.Xor, 0x1507 => LogicOp.Or,
        0x1508 => LogicOp.Nor, 0x1509 => LogicOp.Equiv, 0x150A => LogicOp.Invert, 0x150B => LogicOp.OrReverse,
        0x150C => LogicOp.CopyInverted, 0x150D => LogicOp.OrInverted, 0x150E => LogicOp.Nand, 0x150F => LogicOp.Set,
        _ => LogicOp.Copy,
    };

    private ID3D12PipelineState Pipeline(PixelShaderKey shader, ReadOnlyMemory<byte> ps, GpuDraw d, Target? depth, bool logic)
    {
        var o = d.Ops;
        var blend = o.Blend && !o.LogicOp;
        var mask = (d.ColorMask & 0x0001_0000) != 0 ? ColorWriteEnable.Red : 0;
        if ((d.ColorMask & 0x0000_0100) != 0) mask |= ColorWriteEnable.Green;
        if ((d.ColorMask & 0x0000_0001) != 0) mask |= ColorWriteEnable.Blue;
        if ((d.ColorMask & 0x0100_0000) != 0) mask |= ColorWriteEnable.Alpha;
        var stencil = depth is not null && d.StencilActive;
        if (logic) blend = false;
        var key = string.Join('|', shader.Program, logic ? $"logic{o.LogicOpCode:X}" : "", blend ? $"{o.SrcFactor:X}/{o.DstFactor:X}/{o.Equation:X}" : "noblend", (int)mask,
            depth is null ? "nodepth" : $"{depth.Format}/{d.DepthTest}/{d.DepthWrite}/{d.DepthFunc:X}",
            stencil ? $"{o.StencilFunc:X}/{o.StencilFuncMask:X}/{(o.StencilWrite ? o.StencilWriteMask : 0):X}/{o.StencilFail:X}/{o.StencilZFail:X}/{o.StencilZPass:X}" : "nostencil");
        if (_pipelines.TryGetValue(key, out var cached)) return cached;

        var blendDesc = new BlendDescription(Blend.One, Blend.Zero);
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription(blend, logic,
            BlendFactor(o.SrcFactor, false), BlendFactor(o.DstFactor, false), BlendOp(o.Equation),
            BlendFactor(o.SrcFactor, true), BlendFactor(o.DstFactor, true), BlendOp(o.Equation),
            logic ? Logic(o.LogicOpCode) : LogicOp.Noop, mask);

        // The CPU renderer writes depth whenever depth writes are on, even with the test off: test ALWAYS in that case.
        var depthOn = depth is not null && (d.DepthTest || d.DepthWrite);
        var func = d.DepthTest ? Compare(d.DepthFunc) : ComparisonFunction.Always;
        var face = new DepthStencilOperationDescription(StencilOp(o.StencilFail), StencilOp(o.StencilZFail), StencilOp(o.StencilZPass), Compare(o.StencilFunc));
        var ds = new DepthStencilDescription(depthOn, d.DepthWrite && depth is not null, func,
            stencil, (byte)o.StencilFuncMask, (byte)(o.StencilWrite ? o.StencilWriteMask : 0),
            face.StencilFailOp, face.StencilDepthFailOp, face.StencilPassOp, face.StencilFunc,
            face.StencilFailOp, face.StencilDepthFailOp, face.StencilPassOp, face.StencilFunc);

        var desc = new GraphicsPipelineStateDescription
        {
            RootSignature = _root,
            VertexShader = _vs,
            PixelShader = ps,
            BlendState = blendDesc,
            SampleMask = uint.MaxValue,
            // culling was done by the NV2A side; depth clipping off (z is already in [0,1])
            RasterizerState = new RasterizerDescription(CullMode.None, FillMode.Solid, false, 0, 0, 0, false, false, false, 0, ConservativeRasterizationMode.Off),
            DepthStencilState = ds,
            InputLayout = new InputLayoutDescription(
            [
                new InputElementDescription("POSITION", 0, Format.R32G32B32A32_Float, 0, 0),
                new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 16, 0),
                new InputElementDescription("COLOR", 1, Format.R32G32B32A32_Float, 32, 0),
                new InputElementDescription("TEXCOORD", 4, Format.R32G32B32A32_Float, 48, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32B32A32_Float, 64, 0),
                new InputElementDescription("TEXCOORD", 1, Format.R32G32B32A32_Float, 80, 0),
                new InputElementDescription("TEXCOORD", 2, Format.R32G32B32A32_Float, 96, 0),
                new InputElementDescription("TEXCOORD", 3, Format.R32G32B32A32_Float, 112, 0),
            ]),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = [logic ? Format.R8G8B8A8_UInt : Format.R8G8B8A8_UNorm],
            DepthStencilFormat = depth?.Format ?? Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
        };
        var pso = _device.CreateGraphicsPipelineState(desc);
        _pipelines[key] = pso;
        return pso;
    }

    // ------------------------------------------------------------------ submission

    private static long Align(long v, long a) => (v + a - 1) / a * a;

    private void BeginList()
    {
        if (_open) return;
        _allocator.Reset();
        _list.Reset(_allocator, null!);
        _open = true;
    }

    private void FlushIfOpen()
    {
        if (_open) Guard(Flush);
    }

    /// <summary>Submits the recorded work and waits for it, then recycles the upload and descriptor rings.</summary>
    private void Flush()
    {
        if (!_open) return;
        _list.Close();
        _queue.ExecuteCommandList(_list);
        _open = false;
        WaitIdle();
        _uploadOffset = 0;
        _srvNext = 0;
        _samplerNext = 0;
        foreach (var r in _pendingRelease) r.Dispose();
        _pendingRelease.Clear();
    }

    private void WaitIdle()
    {
        _fenceValue++;
        _queue.Signal(_fence, _fenceValue).CheckError();
        if (_fence.CompletedValue >= _fenceValue) return;
        using var done = new AutoResetEvent(false);
        _fence.SetEventOnCompletion(_fenceValue, done).CheckError();
        done.WaitOne();
    }

    /// <summary>Reads the top-left w x h host pixels of a target (RGBA8, 0xAABBGGRR). Submits and waits.</summary>
    private uint[] ReadTarget(Target target, int w, int h)
    {
        BeginList();
        var rowPitch = (int)Align(w * 4, 256);
        var size = (ulong)(rowPitch * h);
        var buffer = _device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(size, ResourceFlags.None, 0), ResourceStates.CopyDest, null);
        try
        {
            var before = target.State;
            Transition(target, ResourceStates.CopySource);
            var footprint = new PlacedSubresourceFootPrint
            {
                Offset = 0,
                Footprint = new SubresourceFootPrint(Format.R8G8B8A8_Typeless, (uint)w, (uint)h, 1, (uint)rowPitch),
            };
            _list.CopyTextureRegion(new TextureCopyLocation(buffer, footprint), 0, 0, 0, new TextureCopyLocation(target.Resource, 0), new Box(0, 0, 0, w, h, 1));
            Transition(target, before == ResourceStates.CopySource ? ResourceStates.RenderTarget : before);
            Flush();
            void* p;
            buffer.Map(0, &p).CheckError();
            var pixels = new uint[w * h];
            fixed (uint* dst = pixels)
                for (var y = 0; y < h; y++)
                    Buffer.MemoryCopy((byte*)p + (y * rowPitch), dst + (y * w), w * 4, w * 4);
            buffer.Unmap(0, null);
            return pixels;
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>Runs Direct3D work; on failure the backend switches itself off and the CPU picture is used from then on.</summary>
    private void Guard(Action action)
    {
        if (FailureReason is not null || _disposed) return;
        try { action(); }
#pragma warning disable CA1031 // A host-GPU failure must never stop the emulated machine.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            FailureReason = $"{ex.GetType().Name}: {ex.Message}";
            // From here on the CPU renderer draws again (OwnsMemory is false); stop watching guest memory.
            if (_ownsMemory) _memory?.Unwatch();
            try { if (_device.DeviceRemovedReason.Failure) FailureReason += $" (device removed: {_device.DeviceRemovedReason})"; }
#pragma warning disable CA1031
            catch { /* the device itself may be gone */ }
#pragma warning restore CA1031
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { if (_open) Flush(); else WaitIdle(); }
#pragma warning disable CA1031 // Shutting down after a device failure must still release resources.
        catch { }
#pragma warning restore CA1031
        _disposed = true;
        foreach (var t in _colorTargets.Values) t.Resource.Dispose();
        foreach (var t in _depthTargets.Values) t.Resource.Dispose();
        foreach (var t in _textures.Values) t.Resource.Dispose();
        foreach (var p in _pipelines.Values) p.Dispose();
        _scaledCopy.Dispose();
        if (_ownsMemory) _memory?.Unwatch();
        foreach (var r in _pendingRelease) r.Dispose();
        _upload.Unmap(0, null);
        _upload.Dispose();
        _root.Dispose();
        _srvHeap.Dispose();
        _samplerHeap.Dispose();
        _rtvHeap.Dispose();
        _dsvHeap.Dispose();
        _fence.Dispose();
        _list.Dispose();
        _allocator.Dispose();
        _queue.Dispose();
        _device.Dispose();
    }
}
