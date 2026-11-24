/* XPC fixed-function test: original test program for the XBOX PC emulator (nxdk + pbkit).
 * Renders with the NV2A fixed-function transform & lighting pipeline (no vertex program):
 *   composite matrix: window = (2x + 100, 2y + 50, z, 0.5z) / w     (perspective divide by w)
 *   A (40,40)-(120,120)    unlit, vertex colour (0, 0.5, 1)
 *   B (160,40)-(240,120)   lit: scene ambient 0.1, light 0 infinite towards +z, diffuse (0.8,0.4,0.2); normal (0,0,1)
 *   C (280,40)-(360,120)   lit as B, normal (0, 0.8, 0.6)  -> N.L = 0.6
 *   D (400,40)-(480,120)   lit as B, colour material: diffuse from vertex colour (1, 0, 0.5)
 *   E (40,200)-(120,280)   unlit, white, z = 4 (w = 2): object coordinates are twice as far out
 *   F triangle (200,200) (280,280) (200,280) via immediate mode (SET_VERTEX4F, SET_DIFFUSE_COLOR4F), yellow
 * The title prints CPU read-backs of region centres and exits after 2 presented frames. */
#include <hal/video.h>
#include <pbkit/pbkit.h>
#include <pbkit/nv_regs.h>
#include <string.h>
#include <windows.h>
#include <xboxkrnl/xboxkrnl.h>

#define MASK(mask, val) (((val) << (__builtin_ffs(mask) - 1)) & (mask))

typedef struct {
    float pos[4];
    float normal[3];
    float color[3];
} Vertex;

static Vertex *g_v;
static int g_n;

/* object coordinates for a window rectangle at depth z (inverse of the composite transform) */
static void quad(float sx0, float sy0, float sx1, float sy1, float z, const float n[3], float r, float g, float b)
{
    float w = 0.5f * z;
    const float xs[6] = {sx0, sx1, sx1, sx0, sx1, sx0}, ys[6] = {sy0, sy0, sy1, sy0, sy1, sy1};
    for (int i = 0; i < 6; i++) {
        Vertex v = {{(xs[i] * w - 100) / 2, (ys[i] * w - 50) / 2, z, 1}, {n[0], n[1], n[2]}, {r, g, b}};
        g_v[g_n++] = v;
    }
}

static uint32_t *push_floats(uint32_t *p, DWORD method, const float *f, int n)
{
    pb_push(p++, method, n);
    memcpy(p, f, n * 4);
    return p + n;
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

static void draw(int start, int count, int lighting, unsigned int color_material)
{
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_LIGHTING_ENABLE, lighting);
    p = pb_push1(p, NV097_SET_COLOR_MATERIAL, color_material);
    p = pb_push1(p, NV097_SET_BEGIN_END, NV097_SET_BEGIN_END_OP_TRIANGLES);
    p = pb_push1(p, 0x40000000 | NV097_DRAW_ARRAYS, MASK(NV097_DRAW_ARRAYS_COUNT, count - 1) | MASK(NV097_DRAW_ARRAYS_START_INDEX, start));
    p = pb_push1(p, NV097_SET_BEGIN_END, NV097_SET_BEGIN_END_OP_END);
    pb_end(p);
}

static void setup(void)
{
    static const float identity[16] = {1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1};
    /* out[j] = dot(v, row j): x' = 2x + 100w, y' = 2y + 50w, z' = z, w' = 0.5z */
    static const float composite[16] = {2, 0, 0, 100, 0, 2, 0, 50, 0, 0, 1, 0, 0, 0, 0.5f, 0};
    static const float ambient[3] = {0.1f, 0.1f, 0.1f}, zero3[3] = {0, 0, 0}, zero4[4] = {0, 0, 0, 0};
    static const float light_diffuse[3] = {0.8f, 0.4f, 0.2f}, light_dir[3] = {0, 0, 1};

    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_TRANSFORM_EXECUTION_MODE,
                 MASK(NV097_SET_TRANSFORM_EXECUTION_MODE_MODE, NV097_SET_TRANSFORM_EXECUTION_MODE_MODE_FIXED) |
                 MASK(NV097_SET_TRANSFORM_EXECUTION_MODE_RANGE_MODE, NV097_SET_TRANSFORM_EXECUTION_MODE_RANGE_MODE_PRIV));
    p = push_floats(p, NV097_SET_COMPOSITE_MATRIX, composite, 16);
    p = push_floats(p, NV097_SET_MODEL_VIEW_MATRIX, identity, 16);
    p = push_floats(p, NV097_SET_INVERSE_MODEL_VIEW_MATRIX, identity, 16);
    p = push_floats(p, NV097_SET_VIEWPORT_OFFSET, zero4, 4); /* pb_init sets (320.5, 240.5); the composite matrix holds the mapping here */
    pb_end(p);

    p = pb_begin();
    p = push_floats(p, NV097_SET_SCENE_AMBIENT_COLOR, ambient, 3);
    p = push_floats(p, NV097_SET_MATERIAL_EMISSION, zero3, 3);
    p = pb_push1f(p, NV097_SET_MATERIAL_ALPHA, 1.0f);
    p = pb_push1(p, NV097_SET_SPECULAR_ENABLE, 0);
    p = pb_push1(p, NV097_SET_NORMALIZATION_ENABLE, 1);
    p = pb_push1(p, NV097_SET_SKIN_MODE, NV097_SET_SKIN_MODE_OFF);
    p = pb_push1(p, NV097_SET_LIGHT_ENABLE_MASK, NV097_SET_LIGHT_ENABLE_MASK_LIGHT0_INFINITE);
    p = push_floats(p, NV097_SET_LIGHT_AMBIENT_COLOR, zero3, 3);
    p = push_floats(p, NV097_SET_LIGHT_DIFFUSE_COLOR, light_diffuse, 3);
    p = push_floats(p, NV097_SET_LIGHT_SPECULAR_COLOR, zero3, 3);
    p = push_floats(p, NV097_SET_LIGHT_INFINITE_DIRECTION, light_dir, 3);
    p = pb_push1(p, NV097_SET_TEXGEN_S, 0);
    p = pb_push1(p, NV097_SET_TEXGEN_T, 0);
    p = pb_push1(p, NV097_SET_TEXGEN_R, 0);
    p = pb_push1(p, NV097_SET_TEXGEN_Q, 0);
    p = pb_push1(p, NV097_SET_TEXTURE_MATRIX_ENABLE, 0);
    pb_end(p);

    /* combiners: output the interpolated diffuse colour */
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

    static const float nz[3] = {0, 0, 1}, ntilt[3] = {0, 0.8f, 0.6f};
    g_v = MmAllocateContiguousMemoryEx(64 * sizeof(Vertex), 0, 0x3ffb000, 0, PAGE_READWRITE | PAGE_WRITECOMBINE);
    quad(40, 40, 120, 120, 2, nz, 0, 0.5f, 1);      /* A 0..5 */
    quad(160, 40, 240, 120, 2, nz, 1, 1, 1);        /* B 6..11 */
    quad(280, 40, 360, 120, 2, ntilt, 1, 1, 1);     /* C 12..17 */
    quad(400, 40, 480, 120, 2, nz, 1, 0, 0.5f);     /* D 18..23 */
    quad(40, 200, 120, 280, 4, nz, 1, 1, 1);        /* E 24..29 (w = 2) */
}

static void immediate_triangle(void)
{
    /* window (200,200) (280,280) (200,280) at z = 2 (w = 1): object = (window - offset) / 2 */
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_LIGHTING_ENABLE, 0);
    p = pb_push4f(p, NV097_SET_DIFFUSE_COLOR4F, 1, 1, 0, 1);
    p = pb_push1(p, NV097_SET_BEGIN_END, NV097_SET_BEGIN_END_OP_TRIANGLES);
    p = pb_push4f(p, NV097_SET_VERTEX4F, 50, 75, 2, 1);
    p = pb_push4f(p, NV097_SET_VERTEX4F, 90, 115, 2, 1);
    p = pb_push4f(p, NV097_SET_VERTEX4F, 50, 115, 2, 1);
    p = pb_push1(p, NV097_SET_BEGIN_END, NV097_SET_BEGIN_END_OP_END);
    pb_end(p);
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
    pb_end(p);

    p = pb_begin();
    pb_push(p++, NV097_SET_VERTEX_DATA_ARRAY_FORMAT, 16);
    for (int i = 0; i < 16; i++) *(p++) = NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE_F;
    pb_end(p);
    attrib(0, 4, g_v[0].pos);
    attrib(2, 3, g_v[0].normal);
    attrib(3, 3, g_v[0].color);

    draw(0, 6, 0, 0);                                                     /* A */
    draw(6, 12, 1, 0);                                                    /* B, C */
    draw(18, 6, 1, NV097_SET_COLOR_MATERIAL_DIFFUSE_FROM_VERTEX_DIFFUSE); /* D */
    draw(24, 6, 0, 0);                                                    /* E */

    p = pb_begin();
    pb_push(p++, NV097_SET_VERTEX_DATA_ARRAY_FORMAT, 16);
    for (int i = 0; i < 16; i++) *(p++) = NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE_F; /* size 0: immediate values */
    pb_end(p);
    immediate_triangle();
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
        DbgPrint("XPC-FF pb_init failed %d\n", status);
        return 1;
    }
    pb_show_front_screen();
    setup();
    DbgPrint("XPC-FF begin\n");
    for (int frame = 0; frame < 2; frame++) {
        render();
        if (frame == 0) {
            DbgPrint("XPC-FF A %08x B %08x C %08x D %08x E %08x %08x F %08x %08x\n",
                     pixel(80, 80), pixel(200, 80), pixel(320, 80), pixel(440, 80), pixel(80, 240), pixel(125, 240),
                     pixel(220, 270), pixel(270, 210));
        }
        while (pb_finished()) {
        }
    }
    pb_wait_for_vbl();
    pb_wait_for_vbl();
    DbgPrint("XPC-FF end\n");
    return 0;
}
