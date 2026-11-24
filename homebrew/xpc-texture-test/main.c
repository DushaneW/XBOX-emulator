/* XPC texture test: original test program for the XBOX PC emulator (nxdk + pbkit).
 * Draws textured quads through the NV2A texture path; every texel is a known function of its coordinates:
 *   A  (40,40)-(104,104)   swizzled A8R8G8B8 8x8, nearest, clamp:  texel(x,y) = FF, R=x*32, G=y*32, B=(x^y)*32
 *   B  (160,40)-(224,56)   linear R5G6B5 16x4 rect texture, texel coordinates: R5=x, G6=y*16, B5=31-x
 *   C  (280,40)-(344,104)  DXT1 8x8: blocks red / blue / (2*red+blue)/3 / transparent black
 *   D  (400,40)-(528,168)  texture A repeated twice (wrap addressing)
 *   E  (40,200)-(104,264)  texture A modulated by the diffuse colour (0.5, 1, 0.25) in a general combiner stage
 * Each texel covers whole pixel blocks, so block centres have exactly one expected value. The title prints CPU
 * read-backs of block centres from the rendered back buffer and exits after 2 presented frames. */
#include <hal/video.h>
#include <pbkit/pbkit.h>
#include <pbkit/nv_regs.h>
#include <string.h>
#include <windows.h>
#include <xboxkrnl/xboxkrnl.h>

#define MASK(mask, val) (((val) << (__builtin_ffs(mask) - 1)) & (mask))

typedef struct {
    float pos[4];
    float color[3];
    float tex[2];
} Vertex;

static Vertex *g_v;
static int g_n;
static uint32_t *g_texA;   /* swizzled 8x8 A8R8G8B8 */
static uint16_t *g_texB;   /* linear 16x4 R5G6B5, pitch 32 bytes */
static uint8_t *g_texC;    /* DXT1 8x8 = 4 blocks */

/* Morton order for a square power-of-two texture: bit i of x -> bit 2i, bit i of y -> bit 2i+1 */
static unsigned int morton(unsigned int x, unsigned int y)
{
    unsigned int r = 0;
    for (int i = 0; i < 8; i++) r |= ((x >> i) & 1u) << (2 * i) | ((y >> i) & 1u) << (2 * i + 1);
    return r;
}

static void quad(float x0, float y0, float x1, float y1, float u1, float v1, float r, float g, float b)
{
    const float xs[6] = {x0, x1, x1, x0, x1, x0}, ys[6] = {y0, y0, y1, y0, y1, y1};
    const float us[6] = {0, u1, u1, 0, u1, 0}, vs[6] = {0, 0, v1, 0, v1, v1};
    for (int i = 0; i < 6; i++) {
        Vertex v = {{xs[i], ys[i], 10, 1}, {r, g, b}, {us[i], vs[i]}};
        g_v[g_n++] = v;
    }
}

static void make_textures(void)
{
    g_texA = MmAllocateContiguousMemoryEx(8 * 8 * 4, 0, 0x3ffb000, 0, PAGE_READWRITE | PAGE_WRITECOMBINE);
    for (unsigned int y = 0; y < 8; y++)
        for (unsigned int x = 0; x < 8; x++)
            g_texA[morton(x, y)] = 0xFF000000u | (x * 32) << 16 | (y * 32) << 8 | ((x ^ y) * 32);

    g_texB = MmAllocateContiguousMemoryEx(32 * 4, 0, 0x3ffb000, 0, PAGE_READWRITE | PAGE_WRITECOMBINE);
    for (unsigned int y = 0; y < 4; y++)
        for (unsigned int x = 0; x < 16; x++)
            g_texB[y * 16 + x] = (uint16_t)((x << 11) | ((y * 16) << 5) | (31 - x));

    g_texC = MmAllocateContiguousMemoryEx(32, 0, 0x3ffb000, 0, PAGE_READWRITE | PAGE_WRITECOMBINE);
    /* block layout: color0, color1 (RGB565 little endian), 32 bits of 2-bit indices */
    const uint8_t blocks[4][8] = {
        {0x00, 0xF8, 0x1F, 0x00, 0x00, 0x00, 0x00, 0x00}, /* c0 red > c1 blue, all index 0 -> red */
        {0x00, 0xF8, 0x1F, 0x00, 0x55, 0x55, 0x55, 0x55}, /* all index 1 -> blue */
        {0x00, 0xF8, 0x1F, 0x00, 0xAA, 0xAA, 0xAA, 0xAA}, /* all index 2 -> (2*red + blue) / 3 */
        {0x1F, 0x00, 0x00, 0xF8, 0xFF, 0xFF, 0xFF, 0xFF}, /* c0 blue <= c1 red, index 3 -> transparent black */
    };
    memcpy(g_texC, blocks, sizeof(blocks));
}

static void attrib(unsigned int index, unsigned int size, const void *data)
{
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_VERTEX_DATA_ARRAY_FORMAT + index * 4,
                 MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE, NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE_F) |
                 MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_SIZE, size) |
                 MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_STRIDE, sizeof(Vertex)));
    p = pb_push1(p, NV097_SET_VERTEX_DATA_ARRAY_OFFSET + index * 4, (uint32_t)data & 0x03ffffff);
    pb_end(p);
}

/* format: colour format, log2 sizes (swizzled/compressed) or rect size (linear) */
static void texture(const void *data, unsigned int color, unsigned int log2w, unsigned int log2h,
                    unsigned int rect_w, unsigned int rect_h, unsigned int pitch, unsigned int address)
{
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_TEXTURE_OFFSET, (uint32_t)data & 0x03ffffff);
    p = pb_push1(p, NV097_SET_TEXTURE_FORMAT,
                 MASK(NV097_SET_TEXTURE_FORMAT_CONTEXT_DMA, 1) | MASK(NV097_SET_TEXTURE_FORMAT_DIMENSIONALITY, 2) |
                 MASK(NV097_SET_TEXTURE_FORMAT_COLOR, color) | MASK(NV097_SET_TEXTURE_FORMAT_MIPMAP_LEVELS, 1) |
                 MASK(NV097_SET_TEXTURE_FORMAT_BASE_SIZE_U, log2w) | MASK(NV097_SET_TEXTURE_FORMAT_BASE_SIZE_V, log2h) |
                 MASK(NV097_SET_TEXTURE_FORMAT_BASE_SIZE_P, 0));
    p = pb_push1(p, NV097_SET_TEXTURE_ADDRESS, address | (address << 8) | (1 << 16));
    p = pb_push1(p, NV097_SET_TEXTURE_CONTROL0, NV097_SET_TEXTURE_CONTROL0_ENABLE | MASK(NV097_SET_TEXTURE_CONTROL0_MAX_LOD_CLAMP, 1));
    p = pb_push1(p, NV097_SET_TEXTURE_CONTROL1, MASK(NV097_SET_TEXTURE_CONTROL1_IMAGE_PITCH, pitch));
    p = pb_push1(p, NV097_SET_TEXTURE_FILTER, MASK(NV097_SET_TEXTURE_FILTER_MIN, 1) | MASK(NV097_SET_TEXTURE_FILTER_MAG, 1));
    p = pb_push1(p, NV097_SET_TEXTURE_IMAGE_RECT, MASK(NV097_SET_TEXTURE_IMAGE_RECT_WIDTH, rect_w) | MASK(NV097_SET_TEXTURE_IMAGE_RECT_HEIGHT, rect_h));
    pb_end(p);
}

static void combiners(int modulate)
{
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_COMBINER_CONTROL, MASK(NV097_SET_COMBINER_CONTROL_ITERATION_COUNT, 1));
    if (modulate) {
        /* stage 0: r0.rgb = t0.rgb * v0.rgb */
        p = pb_push1(p, NV097_SET_COMBINER_COLOR_ICW, MASK(NV097_SET_COMBINER_COLOR_ICW_A_SOURCE, 0x8) | MASK(NV097_SET_COMBINER_COLOR_ICW_B_SOURCE, 0x4));
        p = pb_push1(p, NV097_SET_COMBINER_COLOR_OCW, MASK(NV097_SET_COMBINER_COLOR_OCW_AB_DST, 0xC));
        p = pb_push1(p, NV097_SET_COMBINER_SPECULAR_FOG_CW0, MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW0_D_SOURCE, 0xC));
    } else {
        p = pb_push1(p, NV097_SET_COMBINER_COLOR_ICW, 0);
        p = pb_push1(p, NV097_SET_COMBINER_COLOR_OCW, 0);
        p = pb_push1(p, NV097_SET_COMBINER_SPECULAR_FOG_CW0, MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW0_D_SOURCE, 0x8));
    }
    p = pb_push1(p, NV097_SET_COMBINER_ALPHA_ICW, 0);
    p = pb_push1(p, NV097_SET_COMBINER_ALPHA_OCW, 0);
    p = pb_push1(p, NV097_SET_COMBINER_SPECULAR_FOG_CW1,
                 MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW1_G_SOURCE, 0x8) | MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW1_G_ALPHA, 1));
    pb_end(p);
}

static void draw(int start, int count)
{
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_BEGIN_END, NV097_SET_BEGIN_END_OP_TRIANGLES);
    p = pb_push1(p, 0x40000000 | NV097_DRAW_ARRAYS, MASK(NV097_DRAW_ARRAYS_COUNT, count - 1) | MASK(NV097_DRAW_ARRAYS_START_INDEX, start));
    p = pb_push1(p, NV097_SET_BEGIN_END, NV097_SET_BEGIN_END_OP_END);
    pb_end(p);
}

static void setup(void)
{
    /* MOV oPos, v0 ; MOV oD0, v3 ; MOV oT0, v9 (final) */
    static const uint32_t program[] = {
        0x00000000, 0x0020001B, 0x0836006C, 0x0000F800,
        0x00000000, 0x0020061B, 0x0836006C, 0x0000F818,
        0x00000000, 0x0020121B, 0x0836006C, 0x0000F849,
    };
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_TRANSFORM_PROGRAM_START, 0);
    p = pb_push1(p, NV097_SET_TRANSFORM_EXECUTION_MODE,
                 MASK(NV097_SET_TRANSFORM_EXECUTION_MODE_MODE, NV097_SET_TRANSFORM_EXECUTION_MODE_MODE_PROGRAM) |
                 MASK(NV097_SET_TRANSFORM_EXECUTION_MODE_RANGE_MODE, NV097_SET_TRANSFORM_EXECUTION_MODE_RANGE_MODE_PRIV));
    p = pb_push1(p, NV097_SET_TRANSFORM_PROGRAM_CXT_WRITE_EN, 0);
    p = pb_push1(p, NV097_SET_TRANSFORM_PROGRAM_LOAD, 0);
    pb_push(p++, NV097_SET_TRANSFORM_PROGRAM, 12);
    memcpy(p, program, sizeof(program));
    p += 12;
    pb_end(p);

    g_v = MmAllocateContiguousMemoryEx(64 * sizeof(Vertex), 0, 0x3ffb000, 0, PAGE_READWRITE | PAGE_WRITECOMBINE);
    quad(40, 40, 104, 104, 1, 1, 1, 1, 1);        /* A: 0..5 */
    quad(160, 40, 224, 56, 16, 4, 1, 1, 1);       /* B: 6..11 (rect: texel coordinates) */
    quad(280, 40, 344, 104, 1, 1, 1, 1, 1);       /* C: 12..17 */
    quad(400, 40, 528, 168, 2, 2, 1, 1, 1);       /* D: 18..23 (wrap) */
    quad(40, 200, 104, 264, 1, 1, 0.5f, 1, 0.25f); /* E: 24..29 (modulate) */
    make_textures();
}

static void render(void)
{
    pb_wait_for_vbl();
    pb_reset();
    pb_target_back_buffer();
    pb_erase_depth_stencil_buffer(0, 0, 640, 480);
    pb_fill(0, 0, 640, 480, 0xFF404040);

    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_CONTROL0, NV097_SET_CONTROL0_TEXTURE_PERSPECTIVE_ENABLE | NV097_SET_CONTROL0_STENCIL_WRITE_ENABLE);
    p = pb_push1(p, NV097_SET_STENCIL_TEST_ENABLE, 0);
    p = pb_push1(p, NV097_SET_DEPTH_TEST_ENABLE, 0);
    p = pb_push1(p, NV097_SET_CULL_FACE_ENABLE, 0);
    p = pb_push1(p, NV097_SET_BLEND_ENABLE, 0);
    p = pb_push1(p, NV097_SET_COLOR_MASK, 0x01010101);
    p = pb_push1(p, NV097_SET_SHADER_STAGE_PROGRAM, MASK(NV097_SET_SHADER_STAGE_PROGRAM_STAGE0, NV097_SET_SHADER_STAGE_PROGRAM_STAGE0_2D_PROJECTIVE));
    pb_end(p);

    p = pb_begin();
    pb_push(p++, NV097_SET_VERTEX_DATA_ARRAY_FORMAT, 16);
    for (int i = 0; i < 16; i++) *(p++) = NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE_F;
    pb_end(p);
    attrib(0, 4, g_v[0].pos);
    attrib(3, 3, g_v[0].color);
    attrib(9, 2, g_v[0].tex);

    combiners(0);
    texture(g_texA, NV097_SET_TEXTURE_FORMAT_COLOR_SZ_A8R8G8B8, 3, 3, 8, 8, 0, 3); /* clamp */
    draw(0, 6);
    texture(g_texB, NV097_SET_TEXTURE_FORMAT_COLOR_LU_IMAGE_R5G6B5, 0, 0, 16, 4, 32, 3);
    draw(6, 6);
    texture(g_texC, NV097_SET_TEXTURE_FORMAT_COLOR_L_DXT1_A1R5G5B5, 3, 3, 8, 8, 0, 3);
    draw(12, 6);
    texture(g_texA, NV097_SET_TEXTURE_FORMAT_COLOR_SZ_A8R8G8B8, 3, 3, 8, 8, 0, 1); /* wrap */
    draw(18, 6);
    combiners(1);
    texture(g_texA, NV097_SET_TEXTURE_FORMAT_COLOR_SZ_A8R8G8B8, 3, 3, 8, 8, 0, 3);
    draw(24, 6);
    while (pb_busy()) {
    }
}

static unsigned int pixel(int x, int y)
{
    DWORD *buffer = pb_back_buffer();
    return buffer[y * (pb_back_buffer_pitch() / 4) + x];
}

int main(void)
{
    XVideoSetMode(640, 480, 32, REFRESH_DEFAULT);
    int status = pb_init();
    if (status) {
        DbgPrint("XPC-TEX pb_init failed %d\n", status);
        return 1;
    }
    pb_show_front_screen();
    setup();
    DbgPrint("XPC-TEX begin\n");
    for (int frame = 0; frame < 2; frame++) {
        render();
        if (frame == 0) {
            DbgPrint("XPC-TEX A %08x %08x %08x B %08x %08x C %08x %08x %08x %08x D %08x %08x E %08x\n",
                     pixel(44, 44), pixel(84, 60), pixel(100, 100), pixel(162, 42), pixel(222, 54),
                     pixel(290, 50), pixel(330, 50), pixel(290, 90), pixel(330, 90),
                     pixel(404, 44), pixel(476, 116), pixel(84, 220));
        }
        while (pb_finished()) {
        }
    }
    pb_wait_for_vbl();
    pb_wait_for_vbl();
    DbgPrint("XPC-TEX end\n");
    return 0;
}
