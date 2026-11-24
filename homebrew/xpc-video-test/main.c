/* XPC video test: original test program for the XBOX PC emulator (nxdk).
 * Sets a 640x480x32 mode, draws a pattern whose every pixel is a known function of (x, y), then waits for 30
 * vertical blanks through nxdk's interrupt path (ISR -> DPC -> NtPulseEvent) and reports the elapsed system time. */
#include <hal/video.h>
#include <windows.h>
#include <xboxkrnl/xboxkrnl.h>

int main(void)
{
    if (!XVideoSetMode(640, 480, 32, REFRESH_DEFAULT)) {
        DbgPrint("XPC-VIDEO XVideoSetMode failed\n");
        return 1;
    }
    VIDEO_MODE vm = XVideoGetMode();
    volatile unsigned int *fb = (volatile unsigned int *)XVideoGetFB();
    for (int y = 0; y < vm.height; y++)
        for (int x = 0; x < vm.width; x++)
            fb[y * vm.width + x] = 0xFF000000u | ((unsigned)(x & 0xFF) << 16) | ((unsigned)(y & 0xFF) << 8) | (unsigned)((x ^ y) & 0xFF);
    XVideoFlushFB();

    LARGE_INTEGER t0, t1;
    KeQuerySystemTime(&t0);
    for (int i = 0; i < 30; i++) XVideoWaitForVBlank();
    KeQuerySystemTime(&t1);
    DbgPrint("XPC-VIDEO mode=%dx%dx%d vblanks=30 elapsed_ms=%d\n", vm.width, vm.height, vm.bpp, (int)((t1.QuadPart - t0.QuadPart) / 10000));
    return 0;
}
