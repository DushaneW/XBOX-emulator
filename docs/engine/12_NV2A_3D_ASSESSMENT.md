# NV2A 3D engine: status

**Status (2026-11-23): started. A CPU reference implementation of the command path and of a subset of the Kelvin 3D
class runs real pbkit programs.** nxdk's own `samples/triangle` (unmodified, third party, not distributed) renders
correctly: blue gradient background, Gouraud-shaded triangle, pbkit's text overlay, frames flipped at vertical
blank. The original test program `homebrew/xpc-3d-test` renders a frame in which every region has a known colour;
the automated test checks it pixel by pixel. `homebrew/xpc-texture-test` covers textures (swizzled, linear,
DXT1; clamp and wrap; texture x diffuse in a combiner stage), 290 928 displayed pixels checked against formulas.
`homebrew/xpc-ff-test` covers the fixed-function pipeline (composite transform with perspective divide, infinite-light
diffuse lighting with scene ambient, tilted normals, colour material, immediate-mode vertices); `homebrew/xpc-ff2-test`
covers the rest (specular with the hardware power curve, fog, object-linear texgen into a texture, blended skinning,
viewport offset); `homebrew/xpc-blend-test` covers blending, alpha test, stencil and logic ops; `homebrew/xpc-2d-test`
covers swizzled and 2x2 supersampled render targets, points, lines, the fill rule, image blits and memory-to-memory
copies.
Commercial games still do not run: mipmaps, cube/3D textures and other features below are missing, many kernel
exports and the APU are missing, and the renderer is far too slow for real time.

## What is implemented (`src/XboxPc.Emulation.Gpu.Nv2a`)
| Part | File | Content |
|---|---|---|
| Register blocks | `Nv2aDevice.cs` | PMC (boot id, interrupt aggregation, level-triggered IRQ 3), PTIMER (counter from virtual time x NVPLL clock x numerator/denominator), PFB/PRAMDAC configuration values (4 partitions, NVPLL 233 MHz), PRMCIO CRTC index/data, PCRTC, 1 MB PRAMIN, USER area. Configuration-only blocks are plain storage |
| PFIFO | `Pfifo.cs` | DMA pusher: increasing / non-increasing headers, old and new jump, call/return, DMA-object fetch with limit check, reserved-command error (interrupt + suspend). RAMHT lookup with the hardware hash. Puller: SET_OBJECT binding, handle->instance translation for methods 0x180-0x1FC, engine per subchannel. Runs synchronously on DMA_PUT; resumes when PGRAPH is released |
| PGRAPH | `Pgraph.cs` | INTR (write-1-to-clear), NSOURCE, TRAPPED_ADDR/DATA, STATUS (busy while stalled), FIFO access, CTX_CONTROL/CTX_USER channel ownership with CONTEXT_SWITCH interrupts, NO_OPERATION software-method traps (pbkit's PB_SETOUTER / PB_FINISHED mechanism), PARAMETER_A/B mirrors |
| Kelvin state | `Pgraph.cs` (`KelvinRenderer`) | every method value stored; surfaces (pitch, A8R8G8B8/X8R8G8B8/R5G6B5/X1R5G5B5 colour, Z24S8/Z16 zeta, clip), clears with rectangle and channel masks, transform program/constant upload with cursors, vertex arrays (F, UB_D3D, UB_OGL, S1, S32K), DRAW_ARRAYS, ARRAY_ELEMENT16/32, INLINE_ARRAY, SET_VERTEX_DATA4F/4UB, diffuse colour |
| Vertex programs | `VertexProgram.cs` | full NV2A VP ISA: MAC (MOV MUL ADD MAD DP3 DPH DP4 DST MIN MAX SLT SGE ARL) and ILU (MOV RCP RCC RSQ EXP LOG LIT), swizzles, negation, A0-relative constants, R12 = oPos, dual temp writes, output/constant writes |
| Primitive assembly + raster | `Pgraph.cs` | points (pixel containing the position), lines and line strips/loops (one-pixel DDA, last pixel excluded), triangles, strips, fans, quads, quad strips, polygons; culling; edge-function rasteriser with the top-left rule; one shared per-fragment path; perspective-correct interpolation in the form a0 + q1·(a1−a0) + q2·(a2−a0) (exact for constant attributes); depth test/write (Z16, Z24); render targets in pitch or swizzled layout (power-of-two size from SET_SURFACE_FORMAT); anti-aliased targets rendered supersampled 2x1 / 2x2 with clears and clip rectangles scaled |
| 2D classes | `TwoDEngine.cs` | per-object state for 0x62 surfaces, 0x9F image blit (SRCCOPY, overlapping rectangles via a buffer, DMA limit checks), 0x39 memory-to-memory (line length/count, pitches); 0x19/0x12/0x72/0x43/0x44 state stored |
| Textures | `Textures.cs`, `Pgraph.cs` | texture shader modes PROGRAM_NONE, 2D_PROJECTIVE (s/q, t/q), PASS_THROUGH; 4 texture units (offset, format, address, control0/1, filter, image rect, border colour); formats Y8, AY8, A8, A8Y8, A1R5G5B5, X1R5G5B5, A4R4G4B4, R5G6B5, A8R8G8B8, X8R8G8B8, A8B8G8R8, B8G8R8A8, R8G8B8A8 (swizzled and linear), DXT1/3/5; swizzle as the hardware interleaves x/y bits; linear textures use texel coordinates; wrap, mirror, clamp to edge, border, clamp; nearest and bilinear (level 0); alpha kill |
| Fixed-function T&L | `FixedFunction.cs` | complete vertex stage as xemu models it: skinning (all 6 modes, blended and explicit weights, model-view/inverse model-view 0-3); window position = (v0 or skinned eye position) · COMPOSITE, x,y,z ÷ w, + VIEWPORT_OFFSET, snapped to 1/16 pixel; lighting in eye space with up to 8 infinite / local / spot lights (attenuation, range, spot falloff (s+L)/(M·s+N)), specular via the hardware curve (x+L)/(M·x+N) on N·H with SPECULAR_PARAMS (power-on defaults 0, −0.494592, 1.494592), infinite-light half vector or local-eye half vectors, separate-specular or merged specular, specular-alpha control; scene ambient (with emission folded in), material emission and alpha, COLOR_MATERIAL for emission/ambient/diffuse/specular; texgen eye-linear, object-linear, sphere map, reflection map, normal map, then texture matrices; fog distance (specular alpha, radial, planar, absolute planar, fog coordinate) and fog factor (linear/exp/exp2 and absolute variants, applied for both pipelines) feeding the combiners' fog register with FOG_COLOR; back colours (v7/v8) used for back faces when two-sided lighting is on; immediate mode SET_VERTEX3F/4F, SET_NORMAL3F, SET_TEXCOORD0_2F/4F |
| Fragment operations | `FragmentOps.cs`, `Pgraph.cs` | GL order: alpha kill -> alpha test (8-bit alpha vs ALPHA_REF, all 8 functions) -> stencil test (Z24S8 low byte, FUNC/REF/FUNC_MASK) -> depth test -> stencil update (FAIL/ZFAIL/ZPASS with KEEP, ZERO, REPLACE, INCR/DECR saturating and wrapping, INVERT; write mask; writes enabled by CONTROL0 bit 0) -> depth write -> blending (all 15 GL factors incl. SRC_ALPHA_SATURATE and constant colour/alpha; ADD, SUBTRACT, REVERSE_SUBTRACT, MIN, MAX; applied to RGB and alpha; destination read from the render target, alpha 1 for X8/R5G6B5 formats) or logic op (all 16, replaces blending) -> colour mask. Early depth rejection only when stencil is off |
| Register combiners | `Combiners.cs` | 8 general stages (RGB + alpha portions, all 8 input mappings, dot products, mux, scale/bias, blue-to-alpha), final combiner with EF product and V1+R0 sum |

Kernel exports added for pbkit: `MmClaimGpuInstanceMemory` (#168), `HalRegisterShutdownNotification` (#47); CPU:
`WBINVD`/`INVD` (no-ops: caches are not modelled).

## Verification
- `tests/XboxPc.Emulation.Tests/Gpu/TwoDTests.cs`: `xpc-2d-test` end to end, swizzled target read back in Morton
  order by the title, supersampled coverage, points, lines (end pixel excluded), the fill rule (an additive quad must
  cover every pixel of its rectangle exactly once, including the shared diagonal), blit and memory copy, and the
  displayed regions pixel by pixel.
- `tests/XboxPc.Emulation.Tests/Gpu/FragmentOpsTests.cs`: every stencil op (saturation and wrap), logic ops, blend
  factors and equations, and `xpc-blend-test` end to end (title read-back and region interiors of the displayed frame,
  expected values derived from the program's definitions including the 8-bit quantisation of the destination).
- `tests/XboxPc.Emulation.Tests/Gpu/FixedFunctionTests.cs`: composite transform and divide, viewport offset and 1/16
  snapping, colour/texcoord pass-through, specular enable/alpha, texture matrix, infinite/local/spot lights, specular
  curve and half vectors (infinite and local eye), every texgen mode, every skinning mode, fog factor modes, colour
  material, and `xpc-ff-test` / `xpc-ff2-test` end to end against values computed from their definitions.
- `tests/XboxPc.Emulation.Tests/Gpu/TextureTests.cs`: swizzle layout (square and non-square), addressing modes,
  format decoding, DXT3/DXT5 alpha, bilinear weights, and `xpc-texture-test` end to end (title read-back and every
  block centre of the displayed frame).
- `tests/XboxPc.Emulation.Tests/Gpu/Nv2aTests.cs`: a vertex program compiled by NVIDIA cgc (from the nxdk triangle
  sample) produces the same result as its Cg source computed independently; hand-assembled program; combiner
  equations; pusher jumps/calls/headers/RAMHT binding; software-method trap -> stall -> acknowledge -> resume;
  context-switch interrupt; reserved-command error; the full `xpc-3d-test` program (title reads back its own
  render target, and the displayed frame is checked against expected colours; Gouraud values against an
  independent barycentric reference).
- Run transcripts of `xpc-3d-test` and `samples/triangle`: `reports/nv2a-runs.txt`.

## Assumptions not yet checked against real hardware
- Pixel centres at integer coordinates (Direct3D 8 convention) and the top-left fill rule. Interior pixels and
  colours are independent of this; exact edge coverage may differ from a real NV2A by one pixel. (The top/bottom
  classification of horizontal edges was inverted until 2026-11-23, found by the 2D test's supersampled corner sample;
  earlier tests only checked interiors.)
- Line rasterisation (DDA, last pixel excluded) and point coverage (size 1) are approximations; anti-aliased targets use
  sample positions on the supersampled grid, not the hardware's sample offsets; how titles resolve AA buffers for
  display (filtered blits) is not modelled beyond SRCCOPY.
- Colour rounding to 8 bits uses round-half-to-even.
- Fixed-function semantics follow xemu's fixed-function shader generator and method handlers (vsh-ff.c, vsh.c,
  pgraph.c, read 2026-11-23): matrices out[j] = dot(v, words[4j..4j+3]) (xemu uploads method words as GLSL columns),
  normals use the inverse model-view as uploaded, the viewport offset is added after the divide, emission is folded
  into the scene-ambient product, alpha follows the diffuse source, and back-side lighting is not computed (back
  colours are the vertex back colours, xemu has the same gap). xemu marks some of this "FIXME: verify"; none of it
  has been compared with a real console here. The test programs confirm the implementation matches these
  definitions, not hardware behaviour. Note: pbkit's `pb_init` sets a viewport offset of (320.53, 240.53), so
  fixed-function programs that use a full-viewport composite matrix must set their own offset.
- W-buffering (`SET_CONTROL0` Z perspective) is not modelled: depth uses `oPos.z` (reported when requested).

## Not implemented (each reported when a title uses it)
Fixed function: back-side lighting computation (back colours are passed through), back specular parameters,
point sprites / point size, VIEWPORT_SCALE (not used by xemu's fixed-function path either).
Mipmap level selection (level 0 is sampled; minification uses the magnification filter), 3D and cube textures,
palettised (I8), YUV and depth texture formats, bump-map and dot-product texture shader modes, colour keys,
anisotropy, signed texture channels; the signed blend equations (FUNC_ADD_SIGNED, REVERSE_SUBTRACT_SIGNED: FUNC_ADD
used, reported); dithering (reported); point size and point sprites; wide or smooth lines; blit operations other than SRCCOPY (ROP,
beta blending, colour key, clip objects); swizzled-surface (0x52) and GDI/text classes; notifiers and semaphores;
context save/restore to instance
memory; multiple simultaneous DMA channels; PTIMER alarm interrupts; the video overlay (PVIDEO).

## Performance (host CPU, Release)
About 110-160 ns per fragment (combiners ~ 110 ns of it), a full 640x480 frame with overdraw takes tens of
milliseconds. The triangle sample runs at roughly 6 emulated frames per real second. The way to real time is the
planned host GPU backend (D3D12/Vulkan, pipeline state keyed by combiner and vertex-program state), with this CPU
renderer kept as the reference oracle for it.

## Next steps (in dependency order)
1. Texture completion: mipmaps with LOD from screen-space derivatives, cube and 3D textures, palettes, the
   remaining texture shader modes.
2. Remaining 2D operations (ROP, beta blending) and notifiers/semaphores used by Direct3D-based titles.
3. Host GPU backend, then PC Boost on top of its output.

## Direct3D 12 backend (host GPU)

`src/XboxPc.Emulation.Gpu.D3D12` renders the Kelvin engine's work on the host GPU (Vortice.Direct3D12/DXGI/D3DCompiler 3.8.3, MIT).
The NV2A side (`IRenderBackend`, `GpuDraw`, `GpuClear`, `GpuBlit`) hands over finished triangles: the vertex stage, primitive
assembly, culling and front/back colour selection stay on the CPU; points become one-pixel quads, lines one-sample-wide quads.

- **Pixel stage:** the register combiners, texture stage modes (2D, pass-through, alpha kill) and the alpha test are generated as
  HLSL (`ShaderGenerator`), a line-by-line translation of `Combiners.Evaluate`. Without blending the shader rounds
  half-to-even like the CPU renderer. Blending, depth, stencil, colour mask and logic ops (UInt render-target view) are
  pipeline state.
- **Resolution scale 1x-6x** (6x = 3840x2880). Anti-aliased targets keep their sample grid (sample i of the guest maps to host
  pixel i x scale).
- **Two modes.** Mirror: the CPU renderer stays authoritative and the GPU draws a copy. Owner (used by the app): the CPU
  rasteriser is skipped. Guest memory is kept coherent through `PhysicalBus.WatchRam`: pages of a GPU-held image are
  watched, so a CPU or device read writes the image back to guest memory first, and a write marks the guest copy newer,
  which is uploaded (scaled up) before the GPU uses it again. 2D blits between GPU-held surfaces run on the GPU. A texture
  whose address is a GPU-held render target is sampled from the host copy at full resolution.
- **Verified:** the six NV2A test programs give the same displayed frames as the CPU renderer (0 pixels differ by more than
  2/255) and the same values in what they read back from their own images, except 7 values that differ by 1/255 where
  blending lands exactly on a rounding tie. Host blend units round ties their own way; the CPU renderer's
  round-half-to-even is a modelling choice, not measured NV2A behaviour. A continuous demo (`homebrew/xpc-spin`) runs at full
  speed, about 60 game frames/s at 2x, against about 3 frames/s with the CPU rasteriser.
- **Not done:** depth/stencil buffers are not written back to guest memory (a title that reads its depth buffer with the CPU
  sees stale data). The vertex stage still runs on the CPU (vertex programs are not translated to shaders). Frames reach the
  window through a read-back per vertical blank, not a swap chain. Mip-mapping, cube/3D textures and the other texture-shader
  modes are not implemented, as in the CPU renderer.
