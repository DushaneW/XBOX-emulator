/* XPC file-system test: original test program for the XBOX PC emulator (nxdk).
 * Exercises the disc drive (D:, read-only) and the data partition (E:) through nxdk's stdio and Win32 layers,
 * and prints every observed result. The expected values follow from the operations themselves. */
#include <nxdk/mount.h>
#include <stdio.h>
#include <string.h>
#include <windows.h>
#include <xboxkrnl/xboxkrnl.h>

static unsigned int checksum(const unsigned char *p, int n)
{
    unsigned int h = 2166136261u;
    for (int i = 0; i < n; i++) { h ^= p[i]; h *= 16777619u; }
    return h;
}

int main(void)
{
    DbgPrint("XPC-FS begin\n");

    /* 1. read a file shipped next to the XBE (D: is mounted by nxdk to the XBE's directory) */
    FILE *in = fopen("D:\\input.txt", "rb");
    char text[64] = {0};
    int n = in ? (int)fread(text, 1, sizeof(text) - 1, in) : -1;
    if (in) fclose(in);
    DbgPrint("read D:input.txt n=%d text=%s\n", n, text);

    /* 2. writing to the disc must fail */
    FILE *ro = fopen("D:\\nope.txt", "wb");
    DbgPrint("write to D: %s\n", ro ? "SUCCEEDED (wrong)" : "refused");
    if (ro) fclose(ro);

    /* 3. directory listing of D: */
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA("D:\\*", &fd);
    int count = 0;
    if (h != INVALID_HANDLE_VALUE) {
        do {
            DbgPrint("D: entry %s dir=%d size=%u\n", fd.cFileName, (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ? 1 : 0, (unsigned)fd.nFileSizeLow);
            count++;
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
    DbgPrint("D: entries=%d\n", count);

    /* 4. E: data partition: create directory, write, read back, seek, rename, delete */
    BOOL mounted = nxMountDrive('E', "\\Device\\Harddisk0\\Partition1\\");
    DbgPrint("mount E: %s\n", mounted ? "ok" : "failed");
    CreateDirectoryA("E:\\xpc", NULL);
    unsigned char block[3000];
    for (int i = 0; i < (int)sizeof(block); i++) block[i] = (unsigned char)(i * 31 + 7);
    FILE *out = fopen("E:\\xpc\\data.bin", "wb");
    size_t w = out ? fwrite(block, 1, sizeof(block), out) : 0;
    if (out) fclose(out);
    DbgPrint("wrote %u bytes checksum=%08x\n", (unsigned)w, checksum(block, sizeof(block)));

    unsigned char back[3000];
    FILE *rb = fopen("E:\\xpc\\data.bin", "rb");
    size_t r = 0;
    int c1000 = -1;
    long size = -1;
    if (rb) {
        r = fread(back, 1, sizeof(back), rb);
        fseek(rb, 1000, SEEK_SET);
        c1000 = fgetc(rb);
        fseek(rb, 0, SEEK_END);
        size = ftell(rb);
        fclose(rb);
    }
    DbgPrint("read back %u bytes checksum=%08x byte1000=%d size=%ld\n", (unsigned)r, checksum(back, (int)r), c1000, size);

    BOOL moved = MoveFileA("E:\\xpc\\data.bin", "E:\\xpc\\renamed.bin");
    DWORD attrsOld = GetFileAttributesA("E:\\xpc\\data.bin");
    DWORD attrsNew = GetFileAttributesA("E:\\xpc\\renamed.bin");
    DbgPrint("rename %s old_exists=%d new_exists=%d\n", moved ? "ok" : "failed", attrsOld != INVALID_FILE_ATTRIBUTES, attrsNew != INVALID_FILE_ATTRIBUTES);

    ULARGE_INTEGER freeBytes, totalBytes, totalFree;
    BOOL space = GetDiskFreeSpaceExA("E:\\", &freeBytes, &totalBytes, &totalFree);
    DbgPrint("disk space query %s nonzero_total=%d\n", space ? "ok" : "failed", space && totalBytes.QuadPart > 0);

    BOOL deleted = DeleteFileA("E:\\xpc\\missing.txt");
    FILE *keep = fopen("E:\\xpc\\keep.txt", "w");
    if (keep) { fputs("kept by xpc-fs-test\n", keep); fclose(keep); }
    DbgPrint("delete of missing file %s; keep.txt written\n", deleted ? "SUCCEEDED (wrong)" : "failed as expected");

    DbgPrint("XPC-FS end\n");
    return 0;
}
