/* XPC Spin: original demo program for the XBOX PC emulator (nxdk + pbkit).
 * Renders continuously until the emulator window is closed: a lit cube rotating about two axes over a vertical
 * gradient, through the real NV2A path (push buffer -> PFIFO -> PGRAPH Kelvin): a two-instruction vertex program
 * (oPos = v0, oD0 = v3), register combiners passing the diffuse colour, vertex arrays, depth test (LESS), and a
 * clear per frame. The program transforms, projects and shades the vertices itself (flat shading from a fixed
 * light) with its own sine approximation, so it needs no maths library. One frame is presented per vertical
 * blank with pbkit's flip; it prints a line every 300 frames. */
#include <hal/video.h>
#include <pbkit/pbkit.h>
#include <pbkit/nv_regs.h>
#include <string.h>
#include <windows.h>
#include <xboxkrnl/xboxkrnl.h>

#define MASK(mask, val) (((val) << (__builtin_ffs(mask) - 1)) & (mask))
#define PI 3.14159265f

typedef struct {
    float pos[4];
    float color[3];
} Vertex;

static Vertex *g_vertices;
static int g_count;

/* sine for any angle: range reduction to [-pi, pi], then a 7th-order odd polynomial (error < 0.0002) */
static float sine(float x)
{
    while (x > PI) x -= 2 * PI;
    while (x < -PI) x += 2 * PI;
    float x2 = x * x;
    return x * (1 - x2 / 6 * (1 - x2 / 20 * (1 - x2 / 42)));
}

static float cosine(float x) { return sine(x + PI / 2); }

static void emit(float x, float y, float z, float r, float g, float b)
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
    while (count > 0) {
        int n = count > 255 ? 255 : count; /* DRAW_ARRAYS count field is 8 bits */
        n -= n % 3;
        p = pb_push1(p, 0x40000000 | NV097_DRAW_ARRAYS,
                     MASK(NV097_DRAW_ARRAYS_COUNT, n - 1) | MASK(NV097_DRAW_ARRAYS_START_INDEX, start));
        start += n;
        count -= n;
    }
    p = pb_push1(p, NV097_SET_BEGIN_END, NV097_SET_BEGIN_END_OP_END);
    pb_end(p);
}

static void setup_pipeline(void)
{
    /* MOV oPos, v0 ; MOV oD0, v3 (final), the same hand-assembled program as xpc-3d-test */
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
    pb_end(p);

    g_vertices = MmAllocateContiguousMemoryEx(512 * sizeof(Vertex), 0, 0x3ffb000, 0, PAGE_READWRITE | PAGE_WRITECOMBINE);
}

/* cube faces: 4 corner indices (counter-clockwise seen from outside) and a base colour */
static const float corners[8][3] = {
    {-1, -1, -1}, {1, -1, -1}, {1, 1, -1}, {-1, 1, -1},
    {-1, -1, 1},  {1, -1, 1},  {1, 1, 1},  {-1, 1, 1},
};
static const int faces[6][4] = {
    {0, 3, 2, 1}, {4, 5, 6, 7}, {0, 1, 5, 4}, {3, 7, 6, 2}, {0, 4, 7, 3}, {1, 2, 6, 5},
};
static const float colors[6][3] = {
    {0.31f, 0.76f, 0.43f}, {0.89f, 0.69f, 0.29f}, {0.35f, 0.55f, 0.95f},
    {0.90f, 0.38f, 0.43f}, {0.75f, 0.75f, 0.78f}, {0.55f, 0.40f, 0.85f},
};

static void build_frame(int frame)
{
    g_count = 0;

    /* background: vertical gradient, far away */
    emit(0, 0, 16000000, 0.04f, 0.05f, 0.07f);
    emit(640, 0, 16000000, 0.04f, 0.05f, 0.07f);
    emit(640, 480, 16000000, 0.10f, 0.13f, 0.12f);
    emit(0, 0, 16000000, 0.04f, 0.05f, 0.07f);
    emit(640, 480, 16000000, 0.10f, 0.13f, 0.12f);
    emit(0, 480, 16000000, 0.10f, 0.13f, 0.12f);

    float a = frame * 0.021f, b = frame * 0.013f;
    float ca = cosine(a), sa = sine(a), cb = cosine(b), sb = sine(b);
    float rot[8][3];
    for (int i = 0; i < 8; i++) {
        float x = corners[i][0], y = corners[i][1], z = corners[i][2];
        float x1 = x * ca + z * sa, z1 = -x * sa + z * ca;   /* about Y */
        float y2 = y * cb - z1 * sb, z2 = y * sb + z1 * cb;  /* about X */
        rot[i][0] = x1;
        rot[i][1] = y2;
        rot[i][2] = z2 + 5.0f;                               /* push away from the eye */
    }
    for (int f = 0; f < 6; f++) {
        const int *q = faces[f];
        /* face normal (rotated) and flat lighting from the upper left */
        float ux = rot[q[1]][0] - rot[q[0]][0], uy = rot[q[1]][1] - rot[q[0]][1], uz = rot[q[1]][2] - rot[q[0]][2];
        float vx = rot[q[3]][0] - rot[q[0]][0], vy = rot[q[3]][1] - rot[q[0]][1], vz = rot[q[3]][2] - rot[q[0]][2];
        float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        if (nz > 0) continue;                                /* back face (facing away from the eye) */
        float len2 = nx * nx + ny * ny + nz * nz;
        float light = (-0.45f * nx - 0.55f * ny - 0.70f * nz);
        light = light <= 0 ? 0 : light * light / len2;       /* squared cosine: soft but contrasted */
        float k = 0.28f + 0.72f * light;
        float px[4], py[4], pz[4];
        for (int c = 0; c < 4; c++) {
            float zc = rot[q[c]][2];
            px[c] = 320 + 430 * rot[q[c]][0] / zc;
            py[c] = 240 - 430 * rot[q[c]][1] / zc;
            pz[c] = zc * 1000000.0f;
        }
        static const int tri[6] = {0, 1, 2, 0, 2, 3};
        for (int t = 0; t < 6; t++) {
            int c = tri[t];
            emit(px[c], py[c], pz[c], colors[f][0] * k, colors[f][1] * k, colors[f][2] * k);
        }
    }
}

static void render_frame(int frame)
{
    build_frame(frame);
    pb_wait_for_vbl();
    pb_reset();
    pb_target_back_buffer();
    pb_erase_depth_stencil_buffer(0, 0, 640, 480);
    pb_fill(0, 0, 640, 480, 0xFF0A0C0E);

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
    while (pb_busy()) {
    }
}

int main(void)
{
    XVideoSetMode(640, 480, 32, REFRESH_DEFAULT);
    int status = pb_init();
    if (status) {
        DbgPrint("XPC-SPIN pb_init failed %d\n", status);
        return 1;
    }
    pb_show_front_screen();
    setup_pipeline();
    DbgPrint("XPC-SPIN begin\n");
    for (int frame = 0;; frame++) {
        render_frame(frame);
        while (pb_finished()) {
        }
        if (frame % 300 == 299) DbgPrint("XPC-SPIN frame %d\n", frame + 1);
    }
}
