/* XPC audio test: original test program for the XBOX PC emulator (nxdk).
 * Streams a deterministic 16-bit stereo signal through nxdk's AC97 HAL (hal/audio.c): buffer descriptors, DMA,
 * completion interrupts, DPC callback refilling a ring of buffers. Every sample is a known function of its index
 * (left: 1 kHz square wave, +-8000; right: (short)(n * 7)), so the emulator's output can be checked sample by sample.
 * 48 buffers of 1600 frames = 1.6 s at 48 kHz. */
#include <hal/audio.h>
#include <windows.h>
#include <xboxkrnl/xboxkrnl.h>

#define FRAMES_PER_BUFFER 1600
#define RING 4
#define TOTAL_BUFFERS 48

static short *g_ring[RING];
static volatile int g_provided;
static volatile int g_callbacks;

static void fill(short *buf, int index)
{
    for (int f = 0; f < FRAMES_PER_BUFFER; f++) {
        int n = index * FRAMES_PER_BUFFER + f;
        buf[2 * f] = ((n / 24) & 1) ? 8000 : -8000;
        buf[2 * f + 1] = (short)(n * 7);
    }
}

static void provide_next(void)
{
    int i = g_provided;
    short *buf = g_ring[i % RING];
    fill(buf, i);
    XAudioProvideSamples((unsigned char *)buf, FRAMES_PER_BUFFER * 4, i == TOTAL_BUFFERS - 1);
    g_provided = i + 1;
}

static void on_buffer_done(void *ac97, void *data)
{
    (void)ac97;
    (void)data;
    g_callbacks++;
    if (g_provided < TOTAL_BUFFERS) provide_next();
}

int main(void)
{
    for (int i = 0; i < RING; i++) g_ring[i] = MmAllocateContiguousMemory(FRAMES_PER_BUFFER * 4);
    XAudioInit(16, 2, on_buffer_done, NULL);
    for (int i = 0; i < RING - 1; i++) provide_next();

    LARGE_INTEGER t0, t1;
    KeQuerySystemTime(&t0);
    XAudioPlay();
    do {
        Sleep(5);
        KeQuerySystemTime(&t1);
    } while (g_callbacks < TOTAL_BUFFERS && (t1.QuadPart - t0.QuadPart) < 50000000LL);
    XAudioPause();

    DbgPrint("XPC-AUDIO buffers=%d callbacks=%d elapsed_ms=%d\n", g_provided, g_callbacks, (int)((t1.QuadPart - t0.QuadPart) / 10000));
    return 0;
}
