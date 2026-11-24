/*
 * XPC probe: original Original-Xbox homebrew test program for the XBOX PC emulator.
 * Written for this project; built with the open-source nxdk SDK. It prints values that are computed at run time
 * through the kernel's DbgPrint, so the emulator's output can be compared with the same source compiled for the
 * host PC (XPC_HOST build), which serves as an independent oracle.
 */
#include <stdint.h>

#ifdef XPC_HOST
#include <stdio.h>
#define DbgPrint printf
#else
#include <xboxkrnl/xboxkrnl.h>
#endif

/* volatile inputs: prevent the compiler from folding the results at build time */
static volatile uint32_t seed = 0x12345678u;
static volatile int32_t n_terms = 1000;
static volatile uint64_t big = 0xFEDCBA9876543210ull;
static volatile uint32_t divisor = 97u;

static uint32_t crc32(const uint8_t *data, uint32_t len)
{
    uint32_t crc = 0xFFFFFFFFu;
    for (uint32_t i = 0; i < len; i++) {
        crc ^= data[i];
        for (int b = 0; b < 8; b++) crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1u)));
    }
    return ~crc;
}

static uint8_t buffer[4096];

int main(void)
{
    DbgPrint("XPC-PROBE begin\n");

    /* 1. integer: xorshift-generated buffer + CRC32 */
    uint32_t x = seed;
    for (uint32_t i = 0; i < sizeof(buffer); i++) {
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        buffer[i] = (uint8_t)x;
    }
    DbgPrint("int crc32=%08x last=%08x\n", crc32(buffer, sizeof(buffer)), x);

    /* 2. 64-bit arithmetic: on a 32-bit target this goes through compiler runtime helpers */
    uint64_t q = big / divisor, r = big % divisor;
    DbgPrint("u64 q_hi=%08x q_lo=%08x r=%u\n", (uint32_t)(q >> 32), (uint32_t)q, (uint32_t)r);

    /* 3. double precision: Basel series, scaled to an integer for printing */
    double s = 0.0;
    for (int32_t i = 1; i <= n_terms; i++) s += 1.0 / ((double)i * (double)i);
    DbgPrint("f64 basel_e9=%d\n", (int32_t)(s * 1e9));

    /* 4. single precision */
    float f = 1.0f;
    for (int32_t i = 0; i < n_terms; i++) f = f * 1.0001f + 0.5f / (float)(i + 1);
    DbgPrint("f32 value_e3=%d\n", (int32_t)(f * 1000.0f));

    DbgPrint("XPC-PROBE end\n");
    return 0;
}
