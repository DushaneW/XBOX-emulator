/* XPC 2D test: original test program for the XBOX PC emulator (nxdk + pbkit).
 * Covers NV2A paths beyond plain triangles; every checked value follows from the program's own definitions:
 *   swizzled render target  extra buffer 0 as a 64x64 swizzled A8R8G8B8 target: cleared blue, red quad (0,0)-(32,32),
 *                           green quad (32,32)-(64,64); the CPU reads texels in Morton order
 *   anti-aliased target     extra buffer 1, 2x2 supersampled (SQUARE_OFFSET_4), pitch 5120: cleared black over
 *                           (0,0)-(64,32), white quad (10,10)-(30,20) -> samples x 20..59, y 20..39 are white
 *   points and lines        on the back buffer: points (300,300) (310,300) yellow; line (300,320)-(340,320) cyan;
 *                           line (360,300)-(360,340) magenta (end pixel excluded)
 *   image blit (class 0x9F) red quad (40,40)-(104,104) with a blue quad (72,72)-(104,104) on top, then a 64x64 SRCCOPY
 *                           from (40,40) to (500,200) through the 0x62 surfaces object
 *   memory to memory (0x39) 2 lines of 8 bytes from a 32-byte ramp (input pitch 16) to a buffer (output pitch 8)
 *   fill rule               black base (200,300)-(260,360), then the same quad (two triangles sharing a diagonal) added
 *                           with ONE/ONE blending at 0.5 (-> 0x808080): every pixel x 200..259, y 300..359 is covered exactly once
 * The title prints CPU read-backs and exits after 2 presented frames. */
#include <hal/video.h>
#include <pbkit/pbkit.h>
#include <pbkit/nv_regs.h>
#include <string.h>
#include <windows.h>
#include <xboxkrnl/xboxkrnl.h>

#define MASK(mask, val) (((val) << (__builtin_ffs(mask) - 1)) & (mask))

typedef struct {
    float pos[4];
    float color[4];
} Vertex;

static Vertex *g_v;
static int g_n;
static uint8_t *g_m2m_src, *g_m2m_dst;

static int vtx(float x, float y, float r, float g, float b)
{
    Vertex v = {{x, y, 10, 1}, {r, g, b, 1}};
    g_v[g_n] = v;
    return g_n++;
}

static int quad(float x0, float y0, float x1, float y1, float r, float g, float b)
{
    int start = vtx(x0, y0, r, g, b);
    vtx(x1, y0, r, g, b);
    vtx(x1, y1, r, g, b);
    vtx(x0, y0, r, g, b);
    vtx(x1, y1, r, g, b);
    vtx(x0, y1, r, g, b);
    return start;
}

static void draw(unsigned int op, int start, int count)
{
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_BEGIN_END, op);
    p = pb_push1(p, 0x40000000 | NV097_DRAW_ARRAYS, MASK(NV097_DRAW_ARRAYS_COUNT, count - 1) | MASK(NV097_DRAW_ARRAYS_START_INDEX, start));
    p = pb_push1(p, NV097_SET_BEGIN_END, NV097_SET_BEGIN_END_OP_END);
    pb_end(p);
}

static void state(void)
{
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_CONTROL0, NV097_SET_CONTROL0_TEXTURE_PERSPECTIVE_ENABLE | NV097_SET_CONTROL0_STENCIL_WRITE_ENABLE);
    p = pb_push1(p, NV097_SET_DEPTH_TEST_ENABLE, 0);
    p = pb_push1(p, NV097_SET_DEPTH_MASK, 0);
    p = pb_push1(p, NV097_SET_STENCIL_TEST_ENABLE, 0);
    p = pb_push1(p, NV097_SET_CULL_FACE_ENABLE, 0);
    p = pb_push1(p, NV097_SET_BLEND_ENABLE, 0);
    p = pb_push1(p, NV097_SET_COLOR_MASK, 0x01010101);
    pb_end(p);
}

static void setup(void)
{
    static const uint32_t program[] = {
        0x00000000, 0x0020001B, 0x0836006C, 0x0000F800,
        0x00000000, 0x0020061B, 0x0836006C, 0x0000F819,
    };
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_TRANSFORM_PROGRAM_START, 0);
    p = pb_push1(p, NV097_SET_TRANSFORM_EXECUTION_MODE,
                 MASK(NV097_SET_TRANSFORM_EXECUTION_MODE_MODE, NV097_SET_TRANSFORM_EXECUTION_MODE_MODE_PROGRAM) |
                 MASK(NV097_SET_TRANSFORM_EXECUTION_MODE_RANGE_MODE, NV097_SET_TRANSFORM_EXECUTION_MODE_RANGE_MODE_PRIV));
    p = pb_push1(p, NV097_SET_TRANSFORM_PROGRAM_CXT_WRITE_EN, 0);
    p = pb_push1(p, NV097_SET_TRANSFORM_PROGRAM_LOAD, 0);
    pb_push(p++, NV097_SET_TRANSFORM_PROGRAM, 8);
    memcpy(p, program, sizeof(program));
    p += 8;
    pb_end(p);

    p = pb_begin();
    p = pb_push1(p, NV097_SET_COMBINER_CONTROL, MASK(NV097_SET_COMBINER_CONTROL_ITERATION_COUNT, 1));
    p = pb_push1(p, NV097_SET_COMBINER_COLOR_ICW, 0);
    p = pb_push1(p, NV097_SET_COMBINER_COLOR_OCW, 0);
    p = pb_push1(p, NV097_SET_COMBINER_ALPHA_ICW, 0);
    p = pb_push1(p, NV097_SET_COMBINER_ALPHA_OCW, 0);
    p = pb_push1(p, NV097_SET_COMBINER_SPECULAR_FOG_CW0, MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW0_D_SOURCE, 0x4));
    p = pb_push1(p, NV097_SET_COMBINER_SPECULAR_FOG_CW1,
                 MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW1_G_SOURCE, 0x4) | MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW1_G_ALPHA, 1));
    p = pb_push1(p, NV097_SET_SHADER_STAGE_PROGRAM, 0);
    pb_end(p);

    g_v = MmAllocateContiguousMemoryEx(128 * sizeof(Vertex), 0, 0x3ffb000, 0, PAGE_READWRITE | PAGE_WRITECOMBINE);
    g_m2m_src = MmAllocateContiguousMemoryEx(32, 0, 0x3ffb000, 0, PAGE_READWRITE);
    g_m2m_dst = MmAllocateContiguousMemoryEx(32, 0, 0x3ffb000, 0, PAGE_READWRITE);
    for (int i = 0; i < 32; i++) g_m2m_src[i] = (uint8_t)i;
    memset(g_m2m_dst, 0xEE, 32);
}

static void arrays(void)
{
    uint32_t *p = pb_begin();
    pb_push(p++, NV097_SET_VERTEX_DATA_ARRAY_FORMAT, 16);
    for (int i = 0; i < 16; i++) *(p++) = NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE_F;
    p = pb_push1(p, NV097_SET_VERTEX_DATA_ARRAY_FORMAT,
                 MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE, NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE_F) |
                 MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_SIZE, 4) | MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_STRIDE, sizeof(Vertex)));
    p = pb_push1(p, NV097_SET_VERTEX_DATA_ARRAY_OFFSET, (uint32_t)g_v[0].pos & 0x03ffffff);
    p = pb_push1(p, NV097_SET_VERTEX_DATA_ARRAY_FORMAT + 3 * 4,
                 MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE, NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE_F) |
                 MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_SIZE, 4) | MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_STRIDE, sizeof(Vertex)));
    p = pb_push1(p, NV097_SET_VERTEX_DATA_ARRAY_OFFSET + 3 * 4, (uint32_t)g_v[0].color & 0x03ffffff);
    pb_end(p);
}

static unsigned int morton(unsigned int x, unsigned int y)
{
    unsigned int r = 0;
    for (int i = 0; i < 8; i++) r |= ((x >> i) & 1u) << (2 * i) | ((y >> i) & 1u) << (2 * i + 1);
    return r;
}

static unsigned int sw_texel(int x, int y) { return pb_extra_buffer(0)[morton(x, y)]; }
static unsigned int aa_sample(int x, int y) { return pb_extra_buffer(1)[y * (5120 / 4) + x]; }
static unsigned int bb(int x, int y) { return pb_back_buffer()[y * (pb_back_buffer_pitch() / 4) + x]; }

static void render(int report)
{
    g_n = 0;
    pb_wait_for_vbl();
    pb_reset();

    /* 1. swizzled 64x64 target in extra buffer 0 */
    pb_target_extra_buffer(0);
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_SURFACE_FORMAT,
                 MASK(NV097_SET_SURFACE_FORMAT_COLOR, NV097_SET_SURFACE_FORMAT_COLOR_LE_A8R8G8B8) |
                 MASK(NV097_SET_SURFACE_FORMAT_ZETA, NV097_SET_SURFACE_FORMAT_ZETA_Z24S8) |
                 MASK(NV097_SET_SURFACE_FORMAT_TYPE, NV097_SET_SURFACE_FORMAT_TYPE_SWIZZLE) |
                 MASK(NV097_SET_SURFACE_FORMAT_WIDTH, 6) | MASK(NV097_SET_SURFACE_FORMAT_HEIGHT, 6));
    p = pb_push1(p, NV097_SET_SURFACE_CLIP_HORIZONTAL, 64 << 16);
    p = pb_push1(p, NV097_SET_SURFACE_CLIP_VERTICAL, 64 << 16);
    pb_end(p);
    state();
    arrays();
    pb_fill(0, 0, 64, 64, 0xFF0000FF);
    draw(NV097_SET_BEGIN_END_OP_TRIANGLES, quad(0, 0, 32, 32, 1, 0, 0), 6);
    draw(NV097_SET_BEGIN_END_OP_TRIANGLES, quad(32, 32, 64, 64, 0, 1, 0), 6);

    /* 2. 2x2 supersampled target in extra buffer 1 (pitch 5120 = 1280 samples) */
    pb_target_extra_buffer(1);
    p = pb_begin();
    p = pb_push1(p, NV097_SET_SURFACE_FORMAT,
                 MASK(NV097_SET_SURFACE_FORMAT_COLOR, NV097_SET_SURFACE_FORMAT_COLOR_LE_A8R8G8B8) |
                 MASK(NV097_SET_SURFACE_FORMAT_ZETA, NV097_SET_SURFACE_FORMAT_ZETA_Z24S8) |
                 MASK(NV097_SET_SURFACE_FORMAT_TYPE, NV097_SET_SURFACE_FORMAT_TYPE_PITCH) |
                 MASK(NV097_SET_SURFACE_FORMAT_ANTI_ALIASING, 2));
    p = pb_push1(p, NV097_SET_SURFACE_PITCH, (5120 << 16) | 5120);
    p = pb_push1(p, NV097_SET_SURFACE_CLIP_HORIZONTAL, 64 << 16);
    p = pb_push1(p, NV097_SET_SURFACE_CLIP_VERTICAL, 32 << 16);
    pb_end(p);
    state();
    pb_fill(0, 0, 64, 32, 0xFF000000);
    draw(NV097_SET_BEGIN_END_OP_TRIANGLES, quad(10, 10, 30, 20, 1, 1, 1), 6);

    /* 3. back buffer: quads for the blit, points, lines */
    pb_target_back_buffer();
    p = pb_begin();
    p = pb_push1(p, NV097_SET_SURFACE_CLIP_HORIZONTAL, 640 << 16);
    p = pb_push1(p, NV097_SET_SURFACE_CLIP_VERTICAL, 480 << 16);
    pb_end(p);
    state();
    pb_erase_depth_stencil_buffer(0, 0, 640, 480);
    pb_fill(0, 0, 640, 480, 0xFF404040);
    draw(NV097_SET_BEGIN_END_OP_TRIANGLES, quad(40, 40, 104, 104, 1, 0, 0), 6);
    draw(NV097_SET_BEGIN_END_OP_TRIANGLES, quad(72, 72, 104, 104, 0, 0, 1), 6);
    int pts = vtx(300, 300, 1, 1, 0);
    vtx(310, 300, 1, 1, 0);
    draw(NV097_SET_BEGIN_END_OP_POINTS, pts, 2);
    int l1 = vtx(300, 320, 0, 1, 1);
    vtx(340, 320, 0, 1, 1);
    int l2 = vtx(360, 300, 1, 0, 1);
    vtx(360, 340, 1, 0, 1);
    draw(NV097_SET_BEGIN_END_OP_LINES, l1, 2);
    draw(NV097_SET_BEGIN_END_OP_LINES, l2, 2);
    draw(NV097_SET_BEGIN_END_OP_TRIANGLES, quad(200, 300, 260, 360, 0, 0, 0), 6);
    p = pb_begin();
    p = pb_push1(p, NV097_SET_BLEND_ENABLE, 1);
    p = pb_push1(p, NV097_SET_BLEND_FUNC_SFACTOR, NV097_SET_BLEND_FUNC_SFACTOR_V_ONE);
    p = pb_push1(p, NV097_SET_BLEND_FUNC_DFACTOR, NV097_SET_BLEND_FUNC_DFACTOR_V_ONE);
    p = pb_push1(p, NV097_SET_BLEND_EQUATION, NV097_SET_BLEND_EQUATION_V_FUNC_ADD);
    pb_end(p);
    draw(NV097_SET_BEGIN_END_OP_TRIANGLES, quad(200, 300, 260, 360, 0.5f, 0.5f, 0.5f), 6);
    p = pb_begin();
    p = pb_push1(p, NV097_SET_BLEND_ENABLE, 0);
    pb_end(p);

    /* 4. image blit (subchannel 3: class 0x9F, surfaces object on subchannel 4: class 0x62).
     *    pbkit binds the surfaces' source DMA to object 3 (all RAM) and destination DMA to object 11 (back buffer). */
    p = pb_begin();
    p = pb_push1_to(SUBCH_4, p, 0x300, 0xA);                                  /* A8R8G8B8 */
    p = pb_push1_to(SUBCH_4, p, 0x304, (2560 << 16) | 2560);                  /* pitch dst | src */
    p = pb_push1_to(SUBCH_4, p, 0x308, (uint32_t)pb_back_buffer() & 0x03ffffff); /* source: physical address */
    p = pb_push1_to(SUBCH_4, p, 0x30C, 0);                                    /* destination: back buffer start */
    p = pb_push1_to(SUBCH_3, p, 0x2FC, 3);                                    /* SRCCOPY */
    p = pb_push1_to(SUBCH_3, p, 0x300, (40 << 16) | 40);                      /* point in (y<<16 | x) */
    p = pb_push1_to(SUBCH_3, p, 0x304, (200 << 16) | 500);                    /* point out */
    p = pb_push1_to(SUBCH_3, p, 0x308, (64 << 16) | 64);                      /* size -> copy */
    pb_end(p);

    /* 5. memory to memory (subchannel 2: class 0x39), DMA object handle 3 = all RAM */
    p = pb_begin();
    p = pb_push1_to(SUBCH_2, p, 0x184, 3);
    p = pb_push1_to(SUBCH_2, p, 0x188, 3);
    p = pb_push1_to(SUBCH_2, p, 0x30C, (uint32_t)g_m2m_src & 0x03ffffff);
    p = pb_push1_to(SUBCH_2, p, 0x310, (uint32_t)g_m2m_dst & 0x03ffffff);
    p = pb_push1_to(SUBCH_2, p, 0x314, 16);
    p = pb_push1_to(SUBCH_2, p, 0x318, 8);
    p = pb_push1_to(SUBCH_2, p, 0x31C, 8);
    p = pb_push1_to(SUBCH_2, p, 0x320, 2);
    p = pb_push1_to(SUBCH_2, p, 0x324, 0x101);
    p = pb_push1_to(SUBCH_2, p, 0x328, 0);
    pb_end(p);
    while (pb_busy()) {
    }

    if (report) {
        DbgPrint("XPC-2D swizzle %08x %08x %08x %08x aa %08x %08x %08x %08x\n",
                 sw_texel(5, 5), sw_texel(40, 40), sw_texel(40, 5), sw_texel(5, 40),
                 aa_sample(20, 20), aa_sample(59, 39), aa_sample(19, 30), aa_sample(60, 30));
        DbgPrint("XPC-2D points %08x %08x %08x lines %08x %08x %08x %08x %08x %08x %08x\n",
                 bb(300, 300), bb(310, 300), bb(301, 300), bb(300, 320), bb(339, 320), bb(340, 320), bb(320, 321),
                 bb(360, 300), bb(360, 339), bb(360, 340));
        DbgPrint("XPC-2D fill %08x %08x %08x %08x %08x %08x\n",
                 bb(200, 300), bb(259, 359), bb(230, 330), bb(260, 330), bb(230, 360), bb(199, 330));
        DbgPrint("XPC-2D blit %08x %08x %08x m2m %08x %08x %08x %08x\n",
                 bb(510, 210), bb(540, 240), bb(570, 270),
                 ((uint32_t *)g_m2m_dst)[0], ((uint32_t *)g_m2m_dst)[1], ((uint32_t *)g_m2m_dst)[2], ((uint32_t *)g_m2m_dst)[3]);
    }
}

int main(void)
{
    XVideoSetMode(640, 480, 32, REFRESH_DEFAULT);
    pb_extra_buffers(2);
    int status = pb_init();
    if (status) {
        DbgPrint("XPC-2D pb_init failed %d\n", status);
        return 1;
    }
    pb_show_front_screen();
    setup();
    DbgPrint("XPC-2D begin\n");
    for (int frame = 0; frame < 2; frame++) {
        render(frame == 0);
        while (pb_finished()) {
        }
    }
    pb_wait_for_vbl();
    pb_wait_for_vbl();
    DbgPrint("XPC-2D end\n");
    return 0;
}
