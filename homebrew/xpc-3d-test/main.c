/* XPC 3D test: original test program for the XBOX PC emulator (nxdk + pbkit).
 * Renders a frame whose every region has a known expected colour, through the real NV2A path: pbkit push buffer ->
 * PFIFO DMA pusher -> PGRAPH Kelvin class: a hand-assembled two-instruction vertex program (oPos = v0, oD0 = v3),
 * register combiners passing the diffuse colour, vertex arrays, depth test, and rectangle clears.
 *   background clear      0xFF203040
 *   red quad              x 40..199,  y 40..159                       0xFFFF0000
 *   green quad (z=100)    x 240..399, y 260..379  drawn first          0xFF00FF00
 *   yellow quad (z=200)   x 320..479, y 300..419  drawn second; where it overlaps green the depth test (LESS) keeps green
 *   Gouraud triangle      (320,40) red, (240,200) green, (400,200) blue
 *   white clear           x 500..599, y 300..399                       0xFFFFFFFF
 * After the first frame the CPU reads pixels of the rendered back buffer and prints them, then 3 frames are
 * presented through pbkit's flip mechanism and the program exits. */
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
} Vertex;

static Vertex *g_vertices;
static int g_count;

static void quad(float x0, float y0, float x1, float y1, float z, float r, float g, float b)
{
    const float xs[6] = {x0, x1, x1, x0, x1, x0};
    const float ys[6] = {y0, y0, y1, y0, y1, y1};
    for (int i = 0; i < 6; i++) {
        Vertex v = {{xs[i], ys[i], z, 1.0f}, {r, g, b}};
        g_vertices[g_count++] = v;
    }
}

static void vertex(float x, float y, float z, float r, float g, float b)
{
    Vertex v = {{x, y, z, 1.0f}, {r, g, b}};
    g_vertices[g_count++] = v;
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
    p = pb_push1(p, 0x40000000 | NV097_DRAW_ARRAYS,
                 MASK(NV097_DRAW_ARRAYS_COUNT, count - 1) | MASK(NV097_DRAW_ARRAYS_START_INDEX, start));
    p = pb_push1(p, NV097_SET_BEGIN_END, NV097_SET_BEGIN_END_OP_END);
    pb_end(p);
}

static void setup_pipeline(void)
{
    /* Vertex program, hand-assembled (NV2A VP encoding, word 0 unused):
     *   MOV oPos, v0
     *   MOV oD0,  v3   (final) */
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

    /* Combiners: one general stage writing nowhere; final combiner output = D = v0 (diffuse), alpha = v0.a */
    p = pb_begin();
    p = pb_push1(p, NV097_SET_COMBINER_CONTROL, MASK(NV097_SET_COMBINER_CONTROL_ITERATION_COUNT, 1));
    p = pb_push1(p, NV097_SET_COMBINER_COLOR_ICW, 0);
    p = pb_push1(p, NV097_SET_COMBINER_COLOR_OCW, 0);
    p = pb_push1(p, NV097_SET_COMBINER_ALPHA_ICW, 0);
    p = pb_push1(p, NV097_SET_COMBINER_ALPHA_OCW, 0);
    p = pb_push1(p, NV097_SET_COMBINER_SPECULAR_FOG_CW0, MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW0_D_SOURCE, 0x4));
    p = pb_push1(p, NV097_SET_COMBINER_SPECULAR_FOG_CW1,
                 MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW1_G_SOURCE, 0x4) | MASK(NV097_SET_COMBINER_SPECULAR_FOG_CW1_G_ALPHA, 1));
    pb_end(p);

    /* Geometry */
    g_vertices = MmAllocateContiguousMemoryEx(64 * sizeof(Vertex), 0, 0x3ffb000, 0, PAGE_READWRITE | PAGE_WRITECOMBINE);
    quad(40, 40, 200, 160, 50, 1, 0, 0);          /* red */
    quad(240, 260, 400, 380, 100, 0, 1, 0);       /* green, near */
    quad(320, 300, 480, 420, 200, 1, 1, 0);       /* yellow, far: hidden where it overlaps green */
    vertex(320, 40, 10, 1, 0, 0);                 /* Gouraud triangle (clockwise on screen) */
    vertex(400, 200, 10, 0, 0, 1);
    vertex(240, 200, 10, 0, 1, 0);
}

static void render_frame(void)
{
    pb_wait_for_vbl();
    pb_reset();
    pb_target_back_buffer();
    pb_erase_depth_stencil_buffer(0, 0, 640, 480);
    pb_fill(0, 0, 640, 480, 0xFF203040);

    uint32_t *p = pb_begin();
    p = pb_push1(p, NV097_SET_CONTROL0, NV097_SET_CONTROL0_TEXTURE_PERSPECTIVE_ENABLE | NV097_SET_CONTROL0_STENCIL_WRITE_ENABLE);
    p = pb_push1(p, NV097_SET_STENCIL_TEST_ENABLE, 0);
    p = pb_push1(p, NV097_SET_CULL_FACE_ENABLE, 0);
    p = pb_push1(p, NV097_SET_BLEND_ENABLE, 0);
    p = pb_push1(p, NV097_SET_DEPTH_TEST_ENABLE, 1);
    p = pb_push1(p, NV097_SET_DEPTH_FUNC, NV097_SET_DEPTH_FUNC_V_LESS);
    p = pb_push1(p, NV097_SET_DEPTH_MASK, 1);
    p = pb_push1(p, NV097_SET_COLOR_MASK, 0x01010101);
    pb_end(p);

    p = pb_begin();
    pb_push(p++, NV097_SET_VERTEX_DATA_ARRAY_FORMAT, 16);
    for (int i = 0; i < 16; i++) *(p++) = NV097_SET_VERTEX_DATA_ARRAY_FORMAT_TYPE_F;
    pb_end(p);
    attrib(0, 4, g_vertices[0].pos);
    attrib(3, 3, g_vertices[0].color);
    draw(0, g_count);

    pb_fill(500, 300, 100, 100, 0xFFFFFFFF);
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
        DbgPrint("XPC-3D pb_init failed %d\n", status);
        return 1;
    }
    pb_show_front_screen();
    setup_pipeline();
    DbgPrint("XPC-3D begin\n");

    for (int frame = 0; frame < 3; frame++) {
        render_frame();
        if (frame == 0) {
            DbgPrint("XPC-3D background %08x red %08x green %08x yellow %08x overlap %08x white %08x\n",
                     pixel(10, 10), pixel(100, 100), pixel(260, 280), pixel(450, 400), pixel(350, 350), pixel(550, 350));
            DbgPrint("XPC-3D triangle top %08x left %08x right %08x centre %08x outside %08x\n",
                     pixel(320, 45), pixel(250, 195), pixel(390, 195), pixel(320, 147), pixel(240, 45));
        }
        while (pb_finished()) {
        }
    }
    pb_wait_for_vbl();
    pb_wait_for_vbl();
    DbgPrint("XPC-3D end frames=3\n");
    return 0;
}
