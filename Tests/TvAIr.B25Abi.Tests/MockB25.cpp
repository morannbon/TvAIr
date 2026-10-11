#include <windows.h>
#include <cstring>
static unsigned flags;
class Decoder {
    DWORD cookie = 0xB25DEC01;
public:
    virtual BOOL Initialize(DWORD round) { flags |= 1; return cookie == 0xB25DEC01 && round == 4; }
    virtual void Release() { flags |= 8; delete this; }
    virtual BOOL Decode(BYTE* src, DWORD size, BYTE** dst, DWORD* outSize) {
        flags |= 2; *dst = src; *outSize = size; return cookie == 0xB25DEC01 && size == 188 && src[0] == 0x47;
    }
    virtual BOOL Flush(BYTE** dst, DWORD* size) { flags |= 4; *dst = nullptr; *size = 0; return TRUE; }
    virtual BOOL Reset() { return TRUE; }
};
extern "C" __declspec(dllexport) Decoder* __cdecl CreateB25Decoder() { return new Decoder(); }
extern "C" __declspec(dllexport) unsigned __cdecl GetTestFlags() { return flags; }
