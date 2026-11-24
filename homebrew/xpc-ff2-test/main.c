/* XPC fixed-function test 2: original test program for the XBOX PC emulator (nxdk + pbkit).
 * Covers the rest of the NV2A fixed-function pipeline with regions whose expected value is constant:
 * composite = identity, viewport offset (8, 4) for every draw (object coordinates are window - offset).
 *   S1 (40,40)-(104,104)    specular: infinite light, half vector (0,0,1), specular colour (0.5,0.25,0),
 *                           default specular curve (L,M,N) = (0,-0.494592,1.494592) -> pf(1) = 1, merged into oD0
 *   S2 (120,40)-(184,104)   as S1 with half vector (0,0.6,0.8) -> pf(0.8) = 0.8 / (1.494592 - 0.494592*0.8)
 *   F  (200,40)-(264,104)   fog: FOG_X coordinate 0.25, linear mode, params (1, 1) -> factor 0.25,
 *                           final combiner fog.a*v0 + (1-fog.a)*fog.rgb, fog colour red -> (1, .25, .25)
 *   T  (280,40)-(344,104)   object-linear texgen S=(x-280)/64, T=(y-40)/64 into an 8x8 swizzled texture
 *   K  object (40,200)-(104,264), skin mode 1 (2 matrices blended), weight 0.5, matrix 1 = +100 x
 *                           -> drawn at (90,200)-(154,264), green
 * The title prints CPU read-backs and exits after 2 presented frames. */
#include <hal/video.h>
#include <pbkit/pbkit.h>
#include <pbkit/nv_regs.h>
#include <string.h>
#include <windows.h>
#include <xboxkrnl/xboxkrnl.h>

#define MASK(mask, val) (((val) << (__builtin_ffs(mask) - 1)) & (mask))
#define VPX 8.0f
#define VPY 4.0f

typedef struct {
    float pos[4];
    float weight;
    float normal[3];
    float color[3];
    float fog;
} Vertex;

static Vertex *g_v;
static int g_n;
static uint32_t *g_tex;

static unsigned int morton(unsigned int x, unsigned int y)
{
    unsigned int r = 0;
    for (int i = 0; i < 8; i++) r |= ((x >> i) & 1u) << (2 * i) | ((y >> i) & 1u) << (2 * i + 1);
    return r;
}

static void quad(float x0, float y0, float x1, float y1, float r, float g, float b, float fog)
{
    const float xs[6] = {x0, x1, x1, x0, x1, x0}, ys[6] = {y0, y0, y1, y0, y1, y1};
    for (int i = 0; i < 6; i++) {
        Vertex v = {{xs[i] - VPX, ys[i] - VPY, 1, 1}, 0.5f, {0, 0, 1}, {r, g, b}, fog};
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

static void draw(int start, int count)
{
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_BEGIN_END, NV097_SET_BEGIN_END_OP_TRIANGLES);
    p = pb_push1(p, 0x40000000 | NV097_DRAW_ARRAYS, MASK(NV097_DRAW_ARRAYS_COUNT, count - 1) | MASK(NV097_DRAW_ARRAYS_START_INDEX, start));
    p = pb_push1(p, NV097_SET_BEGIN_END, NV097_SET_BEGIN_END_OP_END);
    pb_end(p);
}

/* final combiner: cw0 selects A,B,C,D sources; alpha = v0.a */
static void final_combiner(uint32_t cw0, int texture)
{
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_COMBINER_CONTROL, MASK(NV097_SET_COMBINER_CONTROL_ITERATION_COUNT, 1));
    p = pb_push1(p, NV097_SET_COMBINER_COLOR_ICW, 0);
    p = pb_push1(p, NV097_SET_COMBINER_COLOR_OCW, 0);
    p = pb_push1(p, NV097_SET_COMBINER_ALPHA_ICW, 0);
    p = pb_push1(p, NV097_SET_COMBINER_ALPHA_OCW, 0);
    p = pb_push1(p, NV097_SET_COMBINER_SPECULAR_FOG_CW0, cw0);
    p = pb_push1(p, NV097_SET_COMBINER_SPECULAR_FOG_CW1,
                 MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW1_G_SOURCE, 0x4) | MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW1_G_ALPHA, 1));
    p = pb_push1(p, NV097_SET_SHADER_STAGE_PROGRAM,
                 texture ? MASK(NV097_SET_SHADER_STAGE_PROGRAM_STAGE0, NV097_SET_SHADER_STAGE_PROGRAM_STAGE0_2D_PROJECTIVE) : 0);
    pb_end(p);
}

static void setup(void)
{
    static const float identity[16] = {1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1};
    static const float shift[16] = {1, 0, 0, 100, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1}; /* x' = x + 100w */
    static const float zero3[3] = {0, 0, 0}, dir[3] = {0, 0, 1}, spec[3] = {0.5f, 0.25f, 0};
    static const float viewport[4] = {VPX, VPY, 0, 0};

    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_TRANSFORM_EXECUTION_MODE,
                 MASK(NV097_SET_TRANSFORM_EXECUTION_MODE_MODE, NV097_SET_TRANSFORM_EXECUTION_MODE_MODE_FIXED) |
                 MASK(NV097_SET_TRANSFORM_EXECUTION_MODE_RANGE_MODE, NV097_SET_TRANSFORM_EXECUTION_MODE_RANGE_MODE_PRIV));
    p = push_floats(p, NV097_SET_COMPOSITE_MATRIX, identity, 16);
    p = push_floats(p, NV097_SET_MODEL_VIEW_MATRIX, identity, 16);
    p = push_floats(p, NV097_SET_MODEL_VIEW_MATRIX + 0x40, shift, 16);
    p = push_floats(p, NV097_SET_INVERSE_MODEL_VIEW_MATRIX, identity, 16);
    p = push_floats(p, NV097_SET_INVERSE_MODEL_VIEW_MATRIX + 0x40, identity, 16);
    p = push_floats(p, NV097_SET_VIEWPORT_OFFSET, viewport, 4);
    pb_end(p);

    p = pb_begin();
    p = push_floats(p, NV097_SET_SCENE_AMBIENT_COLOR, zero3, 3);
    p = push_floats(p, NV097_SET_MATERIAL_EMISSION, zero3, 3);
    p = pb_push1f(p, NV097_SET_MATERIAL_ALPHA, 1.0f);
    p = pb_push1(p, NV097_SET_NORMALIZATION_ENABLE, 1);
    p = pb_push1(p, NV097_SET_LIGHT_CONTROL, 0); /* specular included in oD0, infinite viewer */
    p = pb_push1(p, NV097_SET_LIGHT_ENABLE_MASK, NV097_SET_LIGHT_ENABLE_MASK_LIGHT0_INFINITE);
    p = push_floats(p, NV097_SET_LIGHT_AMBIENT_COLOR, zero3, 3);
    p = push_floats(p, NV097_SET_LIGHT_DIFFUSE_COLOR, zero3, 3);
    p = push_floats(p, NV097_SET_LIGHT_SPECULAR_COLOR, spec, 3);
    p = push_floats(p, NV097_SET_LIGHT_INFINITE_DIRECTION, dir, 3);
    p = pb_push1(p, NV097_SET_TEXTURE_MATRIX_ENABLE, 0);
    pb_end(p);

    /* texgen planes for stage 0: S = (x - 280)/64, T = (y - 40)/64 in object space (object = window - offset) */
    static const float plane_s[4] = {1.0f / 64, 0, 0, (VPX - 280) / 64};
    static const float plane_t[4] = {0, 1.0f / 64, 0, (VPY - 40) / 64};
    p = pb_begin();
    p = push_floats(p, NV097_SET_TEXGEN_PLANE_S, plane_s, 4);
    p = push_floats(p, NV097_SET_TEXGEN_PLANE_T, plane_t, 4);
    pb_end(p);

    g_tex = MmAllocateContiguousMemoryEx(8 * 8 * 4, 0, 0x3ffb000, 0, PAGE_READWRITE | PAGE_WRITECOMBINE);
    for (unsigned int y = 0; y < 8; y++)
        for (unsigned int x = 0; x < 8; x++)
            g_tex[morton(x, y)] = 0xFF000000u | (x * 32) << 16 | (y * 32) << 8 | ((x ^ y) * 32);
    p = pb_begin();
    p = pb_push1(p, NV097_SET_TEXTURE_OFFSET, (uint32_t)g_tex & 0x03ffffff);
    p = pb_push1(p, NV097_SET_TEXTURE_FORMAT,
                 MASK(NV097_SET_TEXTURE_FORMAT_CONTEXT_DMA, 1) | MASK(NV097_SET_TEXTURE_FORMAT_DIMENSIONALITY, 2) |
                 MASK(NV097_SET_TEXTURE_FORMAT_COLOR, NV097_SET_TEXTURE_FORMAT_COLOR_SZ_A8R8G8B8) |
                 MASK(NV097_SET_TEXTURE_FORMAT_MIPMAP_LEVELS, 1) | MASK(NV097_SET_TEXTURE_FORMAT_BASE_SIZE_U, 3) |
                 MASK(NV097_SET_TEXTURE_FORMAT_BASE_SIZE_V, 3));
    p = pb_push1(p, NV097_SET_TEXTURE_ADDRESS, 3 | (3 << 8) | (1 << 16));
    p = pb_push1(p, NV097_SET_TEXTURE_CONTROL0, NV097_SET_TEXTURE_CONTROL0_ENABLE | MASK(NV097_SET_TEXTURE_CONTROL0_MAX_LOD_CLAMP, 1));
    p = pb_push1(p, NV097_SET_TEXTURE_FILTER, MASK(NV097_SET_TEXTURE_FILTER_MIN, 1) | MASK(NV097_SET_TEXTURE_FILTER_MAG, 1));
    pb_end(p);

    g_v = MmAllocateContiguousMemoryEx(64 * sizeof(Vertex), 0, 0x3ffb000, 0, PAGE_READWRITE | PAGE_WRITECOMBINE);
    quad(40, 40, 104, 104, 1, 1, 1, 0);       /* S1 0..5 */
    quad(120, 40, 184, 104, 1, 1, 1, 0);      /* S2 6..11 */
    quad(200, 40, 264, 104, 1, 1, 1, 0.25f);  /* F 12..17 */
    quad(280, 40, 344, 104, 1, 1, 1, 0);      /* T 18..23 */
    quad(40, 200, 104, 264, 0, 1, 0, 0);      /* K 24..29 (object coordinates; skinning moves it +50 x) */
}

static void set_lighting(int lighting, int specular, const float half[3])
{
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_LIGHTING_ENABLE, lighting);
    p = pb_push1(p, NV097_SET_SPECULAR_ENABLE, specular);
    p = push_floats(p, NV097_SET_LIGHT_INFINITE_HALF_VECTOR, half, 3);
    pb_end(p);
}

static void render(void)
{
    static const float half1[3] = {0, 0, 1}, half2[3] = {0, 0.6f, 0.8f};
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
    p = pb_push1(p, NV097_SET_SKIN_MODE, NV097_SET_SKIN_MODE_OFF);
    p = pb_push1(p, NV097_SET_FOG_ENABLE, 0);
    p = pb_push1(p, NV097_SET_TEXGEN_S, 0);
    p = pb_push1(p, NV097_SET_TEXGEN_T, 0);
    pb_end(p);

    p = pb_begin();
    pb_push(p++, NV097_SET_VERTEX_DATA_ARRAY_FORMAT, 16);
    for (int i = 0; i < 16; i++) *(p++) = NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE_F;
    pb_end(p);
    attrib(0, 4, g_v[0].pos);
    attrib(1, 1, &g_v[0].weight);
    attrib(2, 3, g_v[0].normal);
    attrib(3, 3, g_v[0].color);
    attrib(5, 1, &g_v[0].fog);

    const uint32_t d_is_v0 = MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW0_D_SOURCE, 0x4);
    final_combiner(d_is_v0, 0);
    set_lighting(1, 1, half1);
    draw(0, 6);                                                    /* S1 */
    set_lighting(1, 1, half2);
    draw(6, 6);                                                    /* S2 */
    set_lighting(0, 0, half1);

    /* fog: A = fog.a, B = v0, C = fog.rgb, D = 0 */
    final_combiner(MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW0_A_SOURCE, 0x3) | MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW0_A_ALPHA, 1) |
                   MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW0_B_SOURCE, 0x4) | MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW0_C_SOURCE, 0x3), 0);
    p = pb_begin();
    p = pb_push1(p, NV097_SET_FOG_ENABLE, 1);
    p = pb_push1(p, NV097_SET_FOG_MODE, NV097_SET_FOG_MODE_V_LINEAR);
    p = pb_push1(p, NV097_SET_FOG_GEN_MODE, NV097_SET_FOG_GEN_MODE_V_FOG_X);
    p = pb_push1(p, NV097_SET_FOG_COLOR, 0xFF0000FF); /* red (R is the low byte) */
    p = pb_push3f(p, NV097_SET_FOG_PARAMS, 1, 1, 0);
    pb_end(p);
    draw(12, 6);                                                   /* F */
    p = pb_begin();
    p = pb_push1(p, NV097_SET_FOG_ENABLE, 0);
    p = pb_push1(p, NV097_SET_TEXGEN_S, NV097_SET_TEXGEN_S_OBJECT_LINEAR);
    p = pb_push1(p, NV097_SET_TEXGEN_T, NV097_SET_TEXGEN_S_OBJECT_LINEAR);
    pb_end(p);
    final_combiner(MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW0_D_SOURCE, 0x8), 1);
    draw(18, 6);                                                   /* T */
    p = pb_begin();
    p = pb_push1(p, NV097_SET_TEXGEN_S, 0);
    p = pb_push1(p, NV097_SET_TEXGEN_T, 0);
    p = pb_push1(p, NV097_SET_SKIN_MODE, NV097_SET_SKIN_MODE_2G);
    pb_end(p);
    final_combiner(d_is_v0, 0);
    draw(24, 6);                                                   /* K */
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
        DbgPrint("XPC-FF2 pb_init failed %d\n", status);
        return 1;
    }
    pb_show_front_screen();
    setup();
    DbgPrint("XPC-FF2 begin\n");
    for (int frame = 0; frame < 2; frame++) {
        render();
        if (frame == 0) {
            DbgPrint("XPC-FF2 S1 %08x S2 %08x F %08x T %08x %08x K %08x %08x\n",
                     pixel(72, 72), pixel(152, 72), pixel(232, 72), pixel(284, 44), pixel(324, 60),
                     pixel(140, 230), pixel(70, 230));
        }
        while (pb_finished()) {
        }
    }
    pb_wait_for_vbl();
    pb_wait_for_vbl();
    DbgPrint("XPC-FF2 end\n");
    return 0;
}
