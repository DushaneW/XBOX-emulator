/* XPC blend test: original test program for the XBOX PC emulator (nxdk + pbkit).
 * Fragment operations after the combiners, each region with a value that follows from its definition
 * (base quads are drawn opaque first, then the operation under test):
 *   1 (40,40)-(104,104)    white base; red a=0.25 blended SRC_ALPHA / ONE_MINUS_SRC_ALPHA, ADD
 *   2 (120,40)-(184,104)   base (0.25,0.5,0.25); src (0.5,0.25,0.5) ONE / ONE, ADD
 *   3 (200,40)-(264,104)   white base; src (0.25,0.5,0.75) ONE / ONE, REVERSE_SUBTRACT
 *   4 (280,40)-(344,104)   base (0.2,0.8,0.5); src (0.6,0.4,0.5), MIN
 *   5 (360,40)-(424,104)   black base; white src CONSTANT_COLOR / ZERO, blend colour 0x80406080 (A=0x80 R=0x40 G=0x60 B=0x80)
 *   6 (40,140)-(104,204)   alpha test GREATER 128: left half alpha 0.3 (rejected), right half alpha 0.7 (passes, yellow)
 *   7 (40,300)-(120,380)   stencil: invisible quad writes stencil = 1 (ALWAYS, REPLACE, colour mask off);
 *                          then (20,280)-(200,400) green where stencil EQUAL 1, then the same area blue where
 *                          NOTEQUAL 1 (op INCR)
 *   8 (440,40)-(504,104)   green base; white src with logic op XOR -> 0x00FF00FF
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

static int quad(float x0, float y0, float x1, float y1, float r, float g, float b, float a)
{
    int start = g_n;
    const float xs[6] = {x0, x1, x1, x0, x1, x0}, ys[6] = {y0, y0, y1, y0, y1, y1};
    for (int i = 0; i < 6; i++) {
        Vertex v = {{xs[i], ys[i], 10, 1}, {r, g, b, a}};
        g_v[g_n++] = v;
    }
    return start;
}

static void draw(int start)
{
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_BEGIN_END, NV097_SET_BEGIN_END_OP_TRIANGLES);
    p = pb_push1(p, 0x40000000 | NV097_DRAW_ARRAYS, MASK(NV097_DRAW_ARRAYS_COUNT, 5) | MASK(NV097_DRAW_ARRAYS_START_INDEX, start));
    p = pb_push1(p, NV097_SET_BEGIN_END, NV097_SET_BEGIN_END_OP_END);
    pb_end(p);
}

static void blend(int enable, unsigned int sf, unsigned int df, unsigned int eq)
{
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_BLEND_ENABLE, enable);
    p = pb_push1(p, NV097_SET_BLEND_FUNC_SFACTOR, sf);
    p = pb_push1(p, NV097_SET_BLEND_FUNC_DFACTOR, df);
    p = pb_push1(p, NV097_SET_BLEND_EQUATION, eq);
    pb_end(p);
}

static void stencil(int enable, unsigned int func, unsigned int ref, unsigned int zpass)
{
    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_STENCIL_TEST_ENABLE, enable);
    p = pb_push1(p, NV097_SET_STENCIL_FUNC, func);
    p = pb_push1(p, NV097_SET_STENCIL_FUNC_REF, ref);
    p = pb_push1(p, NV097_SET_STENCIL_FUNC_MASK, 0xFF);
    p = pb_push1(p, NV097_SET_STENCIL_MASK, 0xFF);
    p = pb_push1(p, NV097_SET_STENCIL_OP_FAIL, NV097_SET_STENCIL_OP_V_KEEP);
    p = pb_push1(p, NV097_SET_STENCIL_OP_ZFAIL, NV097_SET_STENCIL_OP_V_KEEP);
    p = pb_push1(p, NV097_SET_STENCIL_OP_ZPASS, zpass);
    pb_end(p);
}

static void setup(void)
{
    /* MOV oPos, v0 ; MOV oD0, v3 (final) */
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
}

static void render(void)
{
    g_n = 0;
    pb_wait_for_vbl();
    pb_reset();
    pb_target_back_buffer();
    pb_erase_depth_stencil_buffer(0, 0, 640, 480); /* depth max, stencil 0 */
    pb_fill(0, 0, 640, 480, 0xFF404040);

    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_CONTROL0, NV097_SET_CONTROL0_TEXTURE_PERSPECTIVE_ENABLE | NV097_SET_CONTROL0_STENCIL_WRITE_ENABLE);
    p = pb_push1(p, NV097_SET_DEPTH_TEST_ENABLE, 0);
    p = pb_push1(p, NV097_SET_CULL_FACE_ENABLE, 0);
    p = pb_push1(p, NV097_SET_ALPHA_TEST_ENABLE, 0);
    p = pb_push1(p, NV097_SET_LOGIC_OP_ENABLE, 0);
    p = pb_push1(p, NV097_SET_DITHER_ENABLE, 0);
    p = pb_push1(p, NV097_SET_COLOR_MASK, 0x01010101);
    pb_push(p++, NV097_SET_VERTEX_DATA_ARRAY_FORMAT, 16);
    for (int i = 0; i < 16; i++) *(p++) = NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE_F;
    pb_end(p);
    stencil(0, NV097_SET_ALPHA_FUNC_V_ALWAYS, 0, NV097_SET_STENCIL_OP_V_KEEP);

    p = pb_begin();
    p = pb_push1(p, NV097_SET_VERTEX_DATA_ARRAY_FORMAT,
                 MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE, NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE_F) |
                 MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_SIZE, 4) | MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_STRIDE, sizeof(Vertex)));
    p = pb_push1(p, NV097_SET_VERTEX_DATA_ARRAY_OFFSET, (uint32_t)g_v[0].pos & 0x03ffffff);
    p = pb_push1(p, NV097_SET_VERTEX_DATA_ARRAY_FORMAT + 3 * 4,
                 MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE, NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE_F) |
                 MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_SIZE, 4) | MASK(NV097_SET_VERTEX_DATA_ARRAY_FORMAT_STRIDE, sizeof(Vertex)));
    p = pb_push1(p, NV097_SET_VERTEX_DATA_ARRAY_OFFSET + 3 * 4, (uint32_t)g_v[0].color & 0x03ffffff);
    pb_end(p);

    const unsigned int ADD = NV097_SET_BLEND_EQUATION_V_FUNC_ADD;
    blend(0, 1, 0, ADD);
    /* opaque bases */
    draw(quad(40, 40, 104, 104, 1, 1, 1, 1));
    draw(quad(120, 40, 184, 104, 0.25f, 0.5f, 0.25f, 1));
    draw(quad(200, 40, 264, 104, 1, 1, 1, 1));
    draw(quad(280, 40, 344, 104, 0.2f, 0.8f, 0.5f, 1));
    draw(quad(360, 40, 424, 104, 0, 0, 0, 1));
    draw(quad(440, 40, 504, 104, 0, 1, 0, 1));

    blend(1, NV097_SET_BLEND_FUNC_SFACTOR_V_SRC_ALPHA, NV097_SET_BLEND_FUNC_DFACTOR_V_ONE_MINUS_SRC_ALPHA, ADD);
    draw(quad(40, 40, 104, 104, 1, 0, 0, 0.25f));
    blend(1, NV097_SET_BLEND_FUNC_SFACTOR_V_ONE, NV097_SET_BLEND_FUNC_DFACTOR_V_ONE, ADD);
    draw(quad(120, 40, 184, 104, 0.5f, 0.25f, 0.5f, 1));
    blend(1, NV097_SET_BLEND_FUNC_SFACTOR_V_ONE, NV097_SET_BLEND_FUNC_DFACTOR_V_ONE, NV097_SET_BLEND_EQUATION_V_FUNC_REVERSE_SUBTRACT);
    draw(quad(200, 40, 264, 104, 0.25f, 0.5f, 0.75f, 1));
    blend(1, NV097_SET_BLEND_FUNC_SFACTOR_V_ONE, NV097_SET_BLEND_FUNC_DFACTOR_V_ONE, NV097_SET_BLEND_EQUATION_V_MIN);
    draw(quad(280, 40, 344, 104, 0.6f, 0.4f, 0.5f, 1));
    p = pb_begin();
    p = pb_push1(p, NV097_SET_BLEND_COLOR, 0x80406080);
    pb_end(p);
    blend(1, NV097_SET_BLEND_FUNC_SFACTOR_V_CONSTANT_COLOR, NV097_SET_BLEND_FUNC_DFACTOR_V_ZERO, ADD);
    draw(quad(360, 40, 424, 104, 1, 1, 1, 1));
    blend(0, 1, 0, ADD);

    /* logic op XOR on the green base */
    p = pb_begin();
    p = pb_push1(p, NV097_SET_LOGIC_OP_ENABLE, 1);
    p = pb_push1(p, NV097_SET_LOGIC_OP, 0x1506);
    pb_end(p);
    draw(quad(440, 40, 504, 104, 1, 1, 1, 1));
    p = pb_begin();
    p = pb_push1(p, NV097_SET_LOGIC_OP_ENABLE, 0);
    pb_end(p);

    /* alpha test: reference 128, GREATER */
    p = pb_begin();
    p = pb_push1(p, NV097_SET_ALPHA_TEST_ENABLE, 1);
    p = pb_push1(p, NV097_SET_ALPHA_FUNC, NV097_SET_ALPHA_FUNC_V_GREATER);
    p = pb_push1(p, NV097_SET_ALPHA_REF, 128);
    pb_end(p);
    draw(quad(40, 140, 72, 204, 1, 1, 0, 0.3f));
    draw(quad(72, 140, 104, 204, 1, 1, 0, 0.7f));
    p = pb_begin();
    p = pb_push1(p, NV097_SET_ALPHA_TEST_ENABLE, 0);
    pb_end(p);

    /* stencil */
    p = pb_begin();
    p = pb_push1(p, NV097_SET_COLOR_MASK, 0);
    pb_end(p);
    stencil(1, NV097_SET_ALPHA_FUNC_V_ALWAYS, 1, NV097_SET_STENCIL_OP_V_REPLACE);
    draw(quad(40, 300, 120, 380, 1, 1, 1, 1));
    p = pb_begin();
    p = pb_push1(p, NV097_SET_COLOR_MASK, 0x01010101);
    pb_end(p);
    stencil(1, NV097_SET_ALPHA_FUNC_V_EQUAL, 1, NV097_SET_STENCIL_OP_V_KEEP);
    draw(quad(20, 280, 200, 400, 0, 1, 0, 1));
    stencil(1, NV097_SET_ALPHA_FUNC_V_NOTEQUAL, 1, NV097_SET_STENCIL_OP_V_INCR);
    draw(quad(20, 280, 200, 400, 0, 0, 1, 1));
    stencil(0, NV097_SET_ALPHA_FUNC_V_ALWAYS, 0, NV097_SET_STENCIL_OP_V_KEEP);
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
        DbgPrint("XPC-BLEND pb_init failed %d\n", status);
        return 1;
    }
    pb_show_front_screen();
    setup();
    DbgPrint("XPC-BLEND begin\n");
    for (int frame = 0; frame < 2; frame++) {
        render();
        if (frame == 0) {
            DbgPrint("XPC-BLEND 1 %08x 2 %08x 3 %08x 4 %08x 5 %08x 6 %08x %08x 7 %08x %08x %08x 8 %08x\n",
                     pixel(72, 72), pixel(152, 72), pixel(232, 72), pixel(312, 72), pixel(392, 72),
                     pixel(56, 172), pixel(88, 172), pixel(80, 340), pixel(160, 340), pixel(250, 340), pixel(472, 72));
        }
        while (pb_finished()) {
        }
    }
    pb_wait_for_vbl();
    pb_wait_for_vbl();
    DbgPrint("XPC-BLEND end\n");
    return 0;
}
