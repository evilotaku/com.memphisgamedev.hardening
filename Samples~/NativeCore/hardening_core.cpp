// hardening_core — example native core for the INativeCore seam.
//
// This is a GENERIC TEMPLATE, not a shipping core. It exports the ABI the ExampleNativeCore P/Invoke
// wrapper expects and implements only the parts that are not secret (the documented FNV digests).
// The tamper scan, device id and licence verification are STUBS with notes on what a real core does.
// A shipping core implements those behind an LLVM obfuscator (e.g. xollvm) so the techniques are not
// readable in the compiled DLL; none of that belongs in a public sample.

#include <cstdint>
#include <cstring>

#if defined(_WIN32)
#define HC_EXPORT extern "C" __declspec(dllexport)
#else
#define HC_EXPORT extern "C" __attribute__((visibility("default")))
#endif

// Bump when the ABI changes so the managed side can refuse a mismatched core.
HC_EXPORT int hc_abi_version(void) { return 1; }

// FNV-1a-64 over the first len bytes. Documented and not secret.
static uint64_t fnv1a(const uint8_t* data, int32_t len, const uint8_t* salt, int32_t saltLen)
{
    uint64_t h = 1469598103934665603ULL;
    for (int32_t i = 0; i < len; i++) { h ^= data[i]; h *= 1099511628211ULL; }
    for (int32_t i = 0; i < saltLen; i++) { h ^= salt[i]; h *= 1099511628211ULL; }
    return h;
}

// Whole-file digest.
HC_EXPORT int hc_hash_region(const uint8_t* data, int32_t len, uint64_t* out)
{
    if (!out || (!data && len > 0)) return -1;
    *out = fnv1a(data, len, nullptr, 0);
    return 0;
}

// Digest answering a server challenge: the file digest folded with the challenge salt, so each
// answer is unique and cannot be replayed.
HC_EXPORT int hc_challenge_digest(const uint8_t* data, int32_t len, const uint8_t* salt, int32_t saltLen, uint64_t* out)
{
    if (!out || (!data && len > 0)) return -1;
    *out = fnv1a(data, len, salt, saltLen);
    return 0;
}

// In-memory ".text" (code) digest of a loaded module answering the salt. A real core walks the
// module's PE/ELF headers to hash the mapped code section; stubbed here as "not available".
HC_EXPORT int hc_text_digest(const char* /*moduleName*/, int32_t /*nameLen*/,
                             const uint8_t* /*salt*/, int32_t /*saltLen*/, uint64_t* out)
{
    if (out) *out = 0;
    return -1; // not loaded / not supported in this template
}

// Tamper bitmask; 0 = clean. A real core runs its anti-tamper checks here (e.g. debugger present,
// unexpected modules loaded, hooks on hot paths) and sets one bit per finding. Kept clean here.
HC_EXPORT int hc_tamper_scan(uint32_t* out)
{
    if (!out) return -1;
    *out = 0;
    return 0;
}

// A stable per-device id for binding a LAN licence. A real core derives it from durable hardware
// identifiers; this template returns a fixed placeholder.
HC_EXPORT int hc_device_id(char* out, int32_t cap)
{
    const char* id = "example-device-id";
    int32_t n = (int32_t)std::strlen(id);
    if (!out || cap < n) return -1;
    std::memcpy(out, id, (size_t)n);
    return n;
}

// Verify a LAN licence for this build; 0 = valid, negative = a specific failure. A real core verifies
// an ECDSA (P-256) signature over the token with only the PUBLIC key compiled in, checks the build id
// and expiry, and (when bindDevice) the device id. This template accepts any non-empty token.
HC_EXPORT int hc_licence_check(const char* token, int32_t tokenLen,
                               const char* /*buildId*/, int32_t /*buildLen*/,
                               int32_t /*bindDevice*/, char* playerOut, int32_t playerCap, int64_t* expires)
{
    if (!token || tokenLen <= 0) return -1;
    if (expires) *expires = 0;
    if (playerOut && playerCap > 0) playerOut[0] = '\0';
    return 0;
}
