/* XPC input test: original test program for the XBOX PC emulator (nxdk).
 * Runs inside the emulator and shows the controller state the emulated Xbox receives through the real input path:
 * OHCI host controller -> XID controller -> nxdk's xid_driver. Every new report is printed with DbgPrint and drawn
 * on screen (the screen is redrawn only when something changed). When the A button is first seen pressed, a rumble
 * report is sent (OUT endpoint). Runs for 300 frames (5 s at 60 Hz). */
#include <hal/debug.h>
#include <hal/video.h>
#include <string.h>
#include <usbh_lib.h>
#include <windows.h>
#include <xboxkrnl/xboxkrnl.h>
#include <xid_driver.h>

static xid_dev_t *g_pad;
static xid_gamepad_in g_state;
static volatile int g_reports;
static int g_rumbled;

static void on_connect(xid_dev_t *dev, int status)
{
    (void)status;
    if (usbh_xid_get_type(dev) == GAMECONTROLLER_DUKE || usbh_xid_get_type(dev) == GAMECONTROLLER_S) {
        g_pad = dev;
        DbgPrint("XPC-INPUT connected vid=%04x pid=%04x type=%04x\n", dev->idVendor, dev->idProduct, usbh_xid_get_type(dev));
    }
}

static void on_disconnect(xid_dev_t *dev, int status)
{
    (void)status;
    if (dev == g_pad) {
        g_pad = NULL;
        DbgPrint("XPC-INPUT disconnected\n");
    }
}

static void on_report(UTR_T *utr)
{
    if (utr->status != 0 || utr->xfer_len < sizeof(xid_gamepad_in)) return;
    memcpy(&g_state, utr->buff, sizeof(g_state));
    g_reports++;
    DbgPrint("XPC-INPUT report buttons=%04x a=%d b=%d x=%d y=%d black=%d white=%d lt=%d rt=%d lx=%d ly=%d rx=%d ry=%d\n",
             g_state.dButtons, g_state.a, g_state.b, g_state.x, g_state.y, g_state.black, g_state.white, g_state.l, g_state.r,
             g_state.leftStickX, g_state.leftStickY, g_state.rightStickX, g_state.rightStickY);
}

int main(void)
{
    XVideoSetMode(640, 480, 32, REFRESH_DEFAULT);
    usbh_core_init();
    usbh_xid_init();
    usbh_install_xid_conn_callback(on_connect, on_disconnect);
    DbgPrint("XPC-INPUT begin\n");

    int drawn_reports = -1;
    xid_dev_t *drawn_pad = NULL;
    for (int frame = 0; frame < 300; frame++) {
        usbh_pooling_hubs();
        if (g_pad) {
            usbh_xid_read(g_pad, 0, on_report);
            if (!g_rumbled && g_state.a > 0) {
                g_rumbled = 1;
                usbh_xid_rumble(g_pad, 0x1234, 0x5678);
                DbgPrint("XPC-INPUT rumble sent\n");
            }
        }
        if (g_reports != drawn_reports || g_pad != drawn_pad) {
            drawn_reports = g_reports;
            drawn_pad = g_pad;
            debugClearScreen();
            debugPrint("XPC input test - frame %d\n\n", frame);
            debugPrint("controller: %s   reports: %d\n", g_pad ? "connected" : "none", g_reports);
            debugPrint("buttons %04x  A %3d B %3d X %3d Y %3d  Black %3d White %3d\n", g_state.dButtons, g_state.a, g_state.b, g_state.x, g_state.y, g_state.black, g_state.white);
            debugPrint("triggers L %3d R %3d\n", g_state.l, g_state.r);
            debugPrint("left stick %6d %6d   right stick %6d %6d\n", g_state.leftStickX, g_state.leftStickY, g_state.rightStickX, g_state.rightStickY);
        }
        XVideoWaitForVBlank();
    }
    DbgPrint("XPC-INPUT end reports=%d\n", g_reports);
    usbh_core_deinit();
    return 0;
}
