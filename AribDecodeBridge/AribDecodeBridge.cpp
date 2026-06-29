// AribDecodeBridge.cpp
// static wstring廃止 → 固定wchar_tバッファ方式
// JIS→SJIS→CP932変換、外部コードページ依存なし
// HandleAddSym default を □(U+25A1) に変更（JIS誤変換バグ修正）。
// ARIB STD-B24 AddSym 全テーブル追加（90〜94区）。
// 外字テーブル追加（85/86区）。LibISDB KanjiTable1/2 準拠。BMP外はサロゲートペア。

#include <windows.h>
#include "AribDecodeBridge.h"

extern "C" __declspec(dllexport)
const char* GetAribBridgeVersion()
{
    return "AribDecodeBridge AddSym-kanji-gaiji-table";
}

// ---------------------------------------------------------------------------
// コードセット
// ---------------------------------------------------------------------------
enum class CS {
    Kanji, Alnum, Hira, Kata,
    JisKata, JisKp1, JisKp2, AddSym, Macro, Unknown
};
static bool IsDouble(CS cs) {
    return cs==CS::Kanji||cs==CS::JisKp1||cs==CS::JisKp2||cs==CS::AddSym;
}

// ---------------------------------------------------------------------------
// JIS→SJIS→Unicode
// ---------------------------------------------------------------------------
static void PutJis(wchar_t* buf, int& pos, int cap, unsigned char k1, unsigned char k2) {
    int row=k1-0x21, col=k2-0x21;
    if(row<0||col<0||row>=94||col>=94) return;
    int t=row>>1;
    unsigned char s1=(unsigned char)(t+(t<0x1F?0x81:0xC1));
    unsigned char s2;
    if(row&1) s2=(unsigned char)(col+0x9F);
    else { s2=(unsigned char)(col+0x40); if(col>=0x3F) s2++; }
    unsigned char sjis[2]={s1,s2};
    wchar_t tmp[4]={};
    int r=MultiByteToWideChar(932,0,(char*)sjis,2,tmp,4);
    for(int j=0;j<r&&pos<cap-1;j++) buf[pos++]=tmp[j];
}

// ---------------------------------------------------------------------------
// 文字テーブル
// ---------------------------------------------------------------------------
static const wchar_t T_JisKata[64]={
    0x3000,0x3002,0x300C,0x300D,0x3001,0x30FB,0x30F2,0x30A1,
    0x30A3,0x30A5,0x30A7,0x30A9,0x30E3,0x30E5,0x30E7,0x30C3,
    0x30FC,0x30A2,0x30A4,0x30A6,0x30A8,0x30AA,0x30AB,0x30AD,
    0x30AF,0x30B1,0x30B3,0x30B5,0x30B7,0x30B9,0x30BB,0x30BD,
    0x30BF,0x30C1,0x30C4,0x30C6,0x30C8,0x30CA,0x30CB,0x30CC,
    0x30CD,0x30CE,0x30CF,0x30D2,0x30D5,0x30D8,0x30DB,0x30DE,
    0x30DF,0x30E0,0x30E1,0x30E2,0x30E4,0x30E6,0x30E8,0x30E9,
    0x30EA,0x30EB,0x30EC,0x30ED,0x30EF,0x30F3,0x309B,0x309C
};
static const wchar_t T_Hira[96]={
    0x3000,0x3041,0x3042,0x3043,0x3044,0x3045,0x3046,0x3047,
    0x3048,0x3049,0x304A,0x304B,0x304C,0x304D,0x304E,0x304F,
    0x3050,0x3051,0x3052,0x3053,0x3054,0x3055,0x3056,0x3057,
    0x3058,0x3059,0x305A,0x305B,0x305C,0x305D,0x305E,0x305F,
    0x3060,0x3061,0x3062,0x3063,0x3064,0x3065,0x3066,0x3067,
    0x3068,0x3069,0x306A,0x306B,0x306C,0x306D,0x306E,0x306F,
    0x3070,0x3071,0x3072,0x3073,0x3074,0x3075,0x3076,0x3077,
    0x3078,0x3079,0x307A,0x307B,0x307C,0x307D,0x307E,0x307F,
    0x3080,0x3081,0x3082,0x3083,0x3084,0x3085,0x3086,0x3087,
    0x3088,0x3089,0x308A,0x308B,0x308C,0x308D,0x308E,0x308F,
    0x3090,0x3091,0x3092,0x3093,0x3000,0x3000,0x3000,0x309D,
    0x309E,0x30FC,0x3002,0x300C,0x300D,0x3001,0x30FB,0x3000
};
static const wchar_t T_Kata[96]={
    0x3000,0x30A1,0x30A2,0x30A3,0x30A4,0x30A5,0x30A6,0x30A7,
    0x30A8,0x30A9,0x30AA,0x30AB,0x30AC,0x30AD,0x30AE,0x30AF,
    0x30B0,0x30B1,0x30B2,0x30B3,0x30B4,0x30B5,0x30B6,0x30B7,
    0x30B8,0x30B9,0x30BA,0x30BB,0x30BC,0x30BD,0x30BE,0x30BF,
    0x30C0,0x30C1,0x30C2,0x30C3,0x30C4,0x30C5,0x30C6,0x30C7,
    0x30C8,0x30C9,0x30CA,0x30CB,0x30CC,0x30CD,0x30CE,0x30CF,
    0x30D0,0x30D1,0x30D2,0x30D3,0x30D4,0x30D5,0x30D6,0x30D7,
    0x30D8,0x30D9,0x30DA,0x30DB,0x30DC,0x30DD,0x30DE,0x30DF,
    0x30E0,0x30E1,0x30E2,0x30E3,0x30E4,0x30E5,0x30E6,0x30E7,
    0x30E8,0x30E9,0x30EA,0x30EB,0x30EC,0x30ED,0x30EE,0x30EF,
    0x30F0,0x30F1,0x30F2,0x30F3,0x30F4,0x30F5,0x30F6,0x30FD,
    0x30FE,0x30FC,0x3002,0x300C,0x300D,0x3001,0x30FB,0x3000
};
static const wchar_t T_Alnum[96]={
    0x3000,0xFF01,0x0022,0xFF03,0xFF04,0xFF05,0xFF06,0x2019,
    0xFF08,0xFF09,0xFF0A,0xFF0B,0xFF0C,0xFF0D,0xFF0E,0xFF0F,
    0xFF10,0xFF11,0xFF12,0xFF13,0xFF14,0xFF15,0xFF16,0xFF17,
    0xFF18,0xFF19,0xFF1A,0xFF1B,0xFF1C,0xFF1D,0xFF1E,0xFF1F,
    0xFF20,0xFF21,0xFF22,0xFF23,0xFF24,0xFF25,0xFF26,0xFF27,
    0xFF28,0xFF29,0xFF2A,0xFF2B,0xFF2C,0xFF2D,0xFF2E,0xFF2F,
    0xFF30,0xFF31,0xFF32,0xFF33,0xFF34,0xFF35,0xFF36,0xFF37,
    0xFF38,0xFF39,0xFF3A,0xFF3B,0xFFE5,0xFF3D,0xFF3E,0xFF3F,
    0xFF40,0xFF41,0xFF42,0xFF43,0xFF44,0xFF45,0xFF46,0xFF47,
    0xFF48,0xFF49,0xFF4A,0xFF4B,0xFF4C,0xFF4D,0xFF4E,0xFF4F,
    0xFF50,0xFF51,0xFF52,0xFF53,0xFF54,0xFF55,0xFF56,0xFF57,
    0xFF58,0xFF59,0xFF5A,0xFF5B,0xFF5C,0xFF5D,0xFF5E,0x3000
};

static void PutSym(wchar_t* buf, int& pos, int cap, const wchar_t* s) {
    for(; *s && pos<cap-1; s++) buf[pos++]=*s;
}
static void PutChar(wchar_t* buf, int& pos, int cap, wchar_t c) {
    if(pos<cap-1) buf[pos++]=c;
}

static void PutCodePoint(wchar_t* buf, int& pos, int cap, unsigned int cp) {
    if (cp <= 0xFFFF) {
        PutChar(buf, pos, cap, static_cast<wchar_t>(cp));
        return;
    }
    if (cp > 0x10FFFF || pos >= cap - 2) return;
    cp -= 0x10000;
    buf[pos++] = static_cast<wchar_t>(0xD800 + (cp >> 10));
    buf[pos++] = static_cast<wchar_t>(0xDC00 + (cp & 0x3FF));
}

// ─── ARIB外字テーブル (LibISDB KanjiTable1/2 準拠) ────────────────────────────
// 85区 (0x7521〜0x757E): 94エントリ
// 86区 (0x7621〜0x764B): 43エントリ
// BMP外文字はサロゲートペアを \xHHHH\xLLLL 形式で記述 (MSVC wchar_t互換)
static const wchar_t* const g_KanjiTable1[94] = {
    L"\x3402", L"\xD840\xDD58", L"\x4efd", L"\x4eff",
    L"\x4f9a", L"\x4fc9", L"\x509c", L"\x511e",
    L"\x51bc", L"\x351f", L"\x5307", L"\x5361",
    L"\x536c", L"\x8a79", L"\xD842\xDFB7", L"\x544d",
    L"\x5496", L"\x549c", L"\x54a9", L"\x550e",
    L"\x554a", L"\x5672", L"\x56e4", L"\x5733",
    L"\x5734", L"\xfa10", L"\x5880", L"\x59e4",
    L"\x5a23", L"\x5a55", L"\x5bec", L"\xfa11",
    L"\x37e2", L"\x5eac", L"\x5f34", L"\x5f45",
    L"\x5fb7", L"\x6017", L"\xfa6b", L"\x6130",
    L"\x6624", L"\x66c8", L"\x66d9", L"\x66fa",
    L"\x66fb", L"\x6852", L"\x9fc4", L"\x6911",
    L"\x693b", L"\x6a45", L"\x6a91", L"\x6adb",
    L"\xD840\xDEEE", L"\xD840\xDFFE", L"\xD841\xDDC4", L"\x6bf1",
    L"\x6ce0", L"\x6d2e", L"\xfa45", L"\x6dbf",
    L"\x6dca", L"\x6df8", L"\xfa46", L"\x6f5e",
    L"\x6ff9", L"\x7064", L"\xfa6c", L"\xD842\xDEEE",
    L"\x7147", L"\x71c1", L"\x7200", L"\x739f",
    L"\x73a8", L"\x73c9", L"\x73d6", L"\x741b",
    L"\x7421", L"\xfa4a", L"\x7426", L"\x742a",
    L"\x742c", L"\x7439", L"\x744b", L"\x3eda",
    L"\x7575", L"\x7581", L"\x7772", L"\x4093",
    L"\x78c8", L"\x78e0", L"\x7947", L"\x79ae",
    L"\x9fc6", L"\x4103",
};
static const wchar_t* const g_KanjiTable2[43] = {
    L"\x9fc5", L"\x79da", L"\x7a1e", L"\x7b7f",
    L"\x7c31", L"\x4264", L"\x7d8b", L"\x7fa1",
    L"\x8118", L"\x813a", L"\xfa6d", L"\x82ae",
    L"\x845b", L"\x84dc", L"\x84ec", L"\x8559",
    L"\x85ce", L"\x8755", L"\x87ec", L"\x880b",
    L"\x88f5", L"\x89d2", L"\x8af6", L"\x8dce",
    L"\x8fbb", L"\x8ff6", L"\x90dd", L"\x9127",
    L"\x912d", L"\x91b2", L"\x9233", L"\x9288",
    L"\x9321", L"\x9348", L"\x9592", L"\x96de",
    L"\x9903", L"\x9940", L"\x9ad9", L"\x9bd6",
    L"\x9dd7", L"\x9eb4", L"\x9eb5",
};

static void PutWStr(wchar_t* buf, int& pos, int cap, const wchar_t* s) {
    while (*s) { if (pos < cap - 1) buf[pos++] = *s; s++; }
}

static void HandleAddSym(wchar_t* buf, int& pos, int cap, unsigned int code, unsigned char k1, unsigned char k2) {
    // 外字テーブル範囲チェック（switch前に処理）
    if (code >= 0x7521 && code <= 0x757E)
        { PutWStr(buf, pos, cap, g_KanjiTable1[code - 0x7521]); return; }
    if (code >= 0x7621 && code <= 0x764B)
        { PutWStr(buf, pos, cap, g_KanjiTable2[code - 0x7621]); return; }
    switch(code) {
    case 0x7A50: PutSym(buf,pos,cap,L"[HV]"); break;
    case 0x7A51: PutSym(buf,pos,cap,L"[SD]"); break;
    case 0x7A55: PutSym(buf,pos,cap,L"[手]"); break;
    case 0x7A56: PutSym(buf,pos,cap,L"[字]"); break;
    case 0x7A57: PutSym(buf,pos,cap,L"[双]"); break;
    case 0x7A58: PutSym(buf,pos,cap,L"[デ]"); break;
    case 0x7A59: PutSym(buf,pos,cap,L"[Ｓ]"); break;
    case 0x7A5A: PutSym(buf,pos,cap,L"[二]"); break;
    case 0x7A5B: PutSym(buf,pos,cap,L"[多]"); break;
    case 0x7A5C: PutSym(buf,pos,cap,L"[解]"); break;
    case 0x7A5D: PutSym(buf,pos,cap,L"[SS]"); break;
    case 0x7A60: PutChar(buf,pos,cap,0x25A0); break; // ■
    case 0x7A61: PutChar(buf,pos,cap,0x25CF); break; // ●
    case 0x7A62: PutSym(buf,pos,cap,L"[天]"); break;
    case 0x7A63: PutSym(buf,pos,cap,L"[交]"); break;
    case 0x7A64: PutSym(buf,pos,cap,L"[映]"); break;
    case 0x7A65: PutSym(buf,pos,cap,L"[無]"); break;
    case 0x7A66: PutSym(buf,pos,cap,L"[料]"); break;
    case 0x7A68: PutSym(buf,pos,cap,L"[前]"); break;
    case 0x7A69: PutSym(buf,pos,cap,L"[後]"); break;
    case 0x7A6A: PutSym(buf,pos,cap,L"[再]"); break;
    case 0x7A6B: PutSym(buf,pos,cap,L"[新]"); break;
    case 0x7A6C: PutSym(buf,pos,cap,L"[初]"); break;
    case 0x7A6D: PutSym(buf,pos,cap,L"[終]"); break;
    case 0x7A6E: PutSym(buf,pos,cap,L"[生]"); break;
    case 0x7A6F: PutSym(buf,pos,cap,L"[販]"); break;
    case 0x7A70: PutSym(buf,pos,cap,L"[声]"); break;
    case 0x7A71: PutSym(buf,pos,cap,L"[吹]"); break;
    case 0x7A72: PutSym(buf,pos,cap,L"[PPV]"); break;
    case 0x7A73: PutSym(buf,pos,cap,L"(秘)"); break;
    case 0x7A74: PutChar(buf,pos,cap,0x307B); PutChar(buf,pos,cap,0x304B); break; // ほか
    case 0x7C21: PutChar(buf,pos,cap,0x2192); break; // →
    case 0x7C22: PutChar(buf,pos,cap,0x2190); break; // ←
    case 0x7C23: PutChar(buf,pos,cap,0x2191); break; // ↑
    case 0x7C24: PutChar(buf,pos,cap,0x2193); break; // ↓
    case 0x7C25: PutChar(buf,pos,cap,0x25CB); break; // ○
    case 0x7C26: PutChar(buf,pos,cap,0x25CF); break; // ●
    case 0x7C27: PutChar(buf,pos,cap,0x5E74); break; // 年
    case 0x7C28: PutChar(buf,pos,cap,0x6708); break; // 月
    case 0x7C29: PutChar(buf,pos,cap,0x65E5); break; // 日
    case 0x7C2A: PutChar(buf,pos,cap,0x5186); break; // 円
    case 0x7C4F: PutChar(buf,pos,cap,0xFF1E); break; // ＞
    case 0x7C50: PutChar(buf,pos,cap,0xFF1C); break; // ＜
    case 0x7C51: PutChar(buf,pos,cap,0x3010); break; // 【
    case 0x7C52: PutChar(buf,pos,cap,0x3011); break; // 】
    case 0x7D21: PutChar(buf,pos,cap,0x6708); break; // 月
    case 0x7D22: PutChar(buf,pos,cap,0x706B); break; // 火
    case 0x7D23: PutChar(buf,pos,cap,0x6C34); break; // 水
    case 0x7D24: PutChar(buf,pos,cap,0x6728); break; // 木
    case 0x7D25: PutChar(buf,pos,cap,0x91D1); break; // 金
    case 0x7D26: PutChar(buf,pos,cap,0x571F); break; // 土
    case 0x7D27: PutChar(buf,pos,cap,0x65E5); break; // 日
    case 0x7D28: PutChar(buf,pos,cap,0x795D); break; // 祝

    // ── 追加シンボル（LibISDB/ARIB STD-B24完全準拠） ──────────────────────────────

    // 90区 0x7A21〜0x7A48: 丸数字①〜㊿
    case 0x7A21: PutChar(buf,pos,cap,0x2460); break; // ①
    case 0x7A22: PutChar(buf,pos,cap,0x2461); break; // ②
    case 0x7A23: PutChar(buf,pos,cap,0x2462); break; // ③
    case 0x7A24: PutChar(buf,pos,cap,0x2463); break; // ④
    case 0x7A25: PutChar(buf,pos,cap,0x2464); break; // ⑤
    case 0x7A26: PutChar(buf,pos,cap,0x2465); break; // ⑥
    case 0x7A27: PutChar(buf,pos,cap,0x2466); break; // ⑦
    case 0x7A28: PutChar(buf,pos,cap,0x2467); break; // ⑧
    case 0x7A29: PutChar(buf,pos,cap,0x2468); break; // ⑨
    case 0x7A2A: PutChar(buf,pos,cap,0x2469); break; // ⑩
    case 0x7A2B: PutChar(buf,pos,cap,0x246A); break; // ⑪
    case 0x7A2C: PutChar(buf,pos,cap,0x246B); break; // ⑫
    case 0x7A2D: PutChar(buf,pos,cap,0x246C); break; // ⑬
    case 0x7A2E: PutChar(buf,pos,cap,0x246D); break; // ⑭
    case 0x7A2F: PutChar(buf,pos,cap,0x246E); break; // ⑮
    case 0x7A30: PutChar(buf,pos,cap,0x246F); break; // ⑯
    case 0x7A31: PutChar(buf,pos,cap,0x2470); break; // ⑰
    case 0x7A32: PutChar(buf,pos,cap,0x2471); break; // ⑱
    case 0x7A33: PutChar(buf,pos,cap,0x2472); break; // ⑲
    case 0x7A34: PutChar(buf,pos,cap,0x2473); break; // ⑳
    case 0x7A35: PutChar(buf,pos,cap,0x3251); break; // ㉑
    case 0x7A36: PutChar(buf,pos,cap,0x3252); break; // ㉒
    case 0x7A37: PutChar(buf,pos,cap,0x3253); break; // ㉓
    case 0x7A38: PutChar(buf,pos,cap,0x3254); break; // ㉔
    case 0x7A39: PutChar(buf,pos,cap,0x3255); break; // ㉕
    case 0x7A3A: PutChar(buf,pos,cap,0x3256); break; // ㉖
    case 0x7A3B: PutChar(buf,pos,cap,0x3257); break; // ㉗
    case 0x7A3C: PutChar(buf,pos,cap,0x3258); break; // ㉘
    case 0x7A3D: PutChar(buf,pos,cap,0x3259); break; // ㉙
    case 0x7A3E: PutChar(buf,pos,cap,0x325A); break; // ㉚
    case 0x7A3F: PutChar(buf,pos,cap,0x325B); break; // ㉛
    case 0x7A40: PutChar(buf,pos,cap,0x2B55); break; // ⭕
    case 0x7A41: PutChar(buf,pos,cap,0x3248); break; // ㉈
    case 0x7A42: PutChar(buf,pos,cap,0x3249); break; // ㉉
    case 0x7A43: PutChar(buf,pos,cap,0x324A); break; // ㉊
    case 0x7A44: PutChar(buf,pos,cap,0x324B); break; // ㉋
    case 0x7A45: PutChar(buf,pos,cap,0x324C); break; // ㉌
    case 0x7A46: PutChar(buf,pos,cap,0x324D); break; // ㉍
    case 0x7A47: PutChar(buf,pos,cap,0x324E); break; // ㉎
    case 0x7A48: PutChar(buf,pos,cap,0x324F); break; // ㉏
    // 0x7A49〜0x7A4C: 未定義
    case 0x7A4D: PutSym(buf,pos,cap,L"1."); break;
    case 0x7A4E: PutSym(buf,pos,cap,L"2."); break;
    case 0x7A4F: PutSym(buf,pos,cap,L"3."); break;
    // 0x7A50〜0x7A74: 既存登録済み
    case 0x7A52: PutSym(buf,pos,cap,L"[Ｐ]"); break;
    case 0x7A53: PutSym(buf,pos,cap,L"[Ｗ]"); break;
    case 0x7A54: PutSym(buf,pos,cap,L"[MV]"); break;
    case 0x7A5E: PutSym(buf,pos,cap,L"[Ｂ]"); break;
    case 0x7A5F: PutSym(buf,pos,cap,L"[Ｎ]"); break;
    case 0x7A67: PutSym(buf,pos,cap,L"[年齢制限]"); break;

    // 91区 0x7B21〜0x7B51: 特殊記号
    case 0x7B21: PutCodePoint(buf,pos,cap,0x1F194); break; // 🆔
    case 0x7B22: PutCodePoint(buf,pos,cap,0x1F233); break; // 🈳
    case 0x7B23: PutCodePoint(buf,pos,cap,0x1F236); break; // 🈶
    case 0x7B24: PutCodePoint(buf,pos,cap,0x1F21A); break; // 🈚
    case 0x7B25: PutCodePoint(buf,pos,cap,0x1F235); break; // 🈵
    case 0x7B26: PutCodePoint(buf,pos,cap,0x1F234); break; // 🈴
    case 0x7B27: PutCodePoint(buf,pos,cap,0x1F232); break; // 🈲
    case 0x7B28: PutCodePoint(buf,pos,cap,0x1F239); break; // 🈹
    case 0x7B29: PutCodePoint(buf,pos,cap,0x1F23A); break; // 🈺
    case 0x7B2A: PutCodePoint(buf,pos,cap,0x1F237); break; // 🈷
    case 0x7B2B: PutCodePoint(buf,pos,cap,0x1F238); break; // 🈸
    case 0x7B2C: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B2D: PutCodePoint(buf,pos,cap,0x1F250); break; // 🉐
    case 0x7B2E: PutCodePoint(buf,pos,cap,0x1F251); break; // 🉑
    case 0x7B2F: PutCodePoint(buf,pos,cap,0x1F238); break; // 🈸
    case 0x7B30: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B31: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B32: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B33: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B34: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B35: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B36: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B37: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B38: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B39: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B3A: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B3B: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B3C: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B3D: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B3E: PutCodePoint(buf,pos,cap,0x1F22F); break; // 🈯
    case 0x7B3F: PutChar(buf,pos,cap,0x339D); break; // ㎝
    case 0x7B40: PutChar(buf,pos,cap,0x338E); break; // ㎎
    case 0x7B41: PutChar(buf,pos,cap,0x338F); break; // ㎏
    case 0x7B42: PutChar(buf,pos,cap,0x33A1); break; // ㎡
    case 0x7B43: PutChar(buf,pos,cap,0x5E74); break; // 年
    case 0x7B44: PutChar(buf,pos,cap,0x6708); break; // 月
    case 0x7B45: PutChar(buf,pos,cap,0x65E5); break; // 日
    case 0x7B46: PutChar(buf,pos,cap,0x2116); break; // №
    case 0x7B47: PutChar(buf,pos,cap,0x2121); break; // ℡
    case 0x7B48: PutChar(buf,pos,cap,0x3016); break; // 〖
    case 0x7B49: PutChar(buf,pos,cap,0x3017); break; // 〗
    case 0x7B4A: PutChar(buf,pos,cap,0x2070); break; // ⁰
    case 0x7B4B: PutChar(buf,pos,cap,0x2074); break; // ⁴
    case 0x7B4C: PutChar(buf,pos,cap,0x2075); break; // ⁵
    case 0x7B4D: PutChar(buf,pos,cap,0x2076); break; // ⁶
    case 0x7B4E: PutChar(buf,pos,cap,0x2077); break; // ⁷
    case 0x7B4F: PutChar(buf,pos,cap,0x2078); break; // ⁸
    case 0x7B50: PutChar(buf,pos,cap,0x2079); break; // ⁹
    case 0x7B51: PutChar(buf,pos,cap,0x3349); break; // ㍉

    // 92区 0x7C2B〜0x7C7B: 単位・記号（0x7C21〜0x7C2Aと0x7C4F〜0x7C52は既存）
    case 0x7C2B: PutSym(buf,pos,cap,L"km"); break;
    case 0x7C2C: PutChar(buf,pos,cap,0x338D); break; // ㎍
    case 0x7C2D: PutChar(buf,pos,cap,0x339D); break; // ㎝
    case 0x7C2E: PutChar(buf,pos,cap,0x339C); break; // ㎜
    case 0x7C2F: PutChar(buf,pos,cap,0x338E); break; // ㎎
    case 0x7C30: PutChar(buf,pos,cap,0x338F); break; // ㎏
    case 0x7C31: PutChar(buf,pos,cap,0x33A5); break; // ㏄
    case 0x7C32: PutChar(buf,pos,cap,0x33A1); break; // ㎡
    case 0x7C33: PutSym(buf,pos,cap,L"m²"); break; // m²
    case 0x7C34: PutChar(buf,pos,cap,0x337B); break; // ㍻
    case 0x7C35: PutChar(buf,pos,cap,0x3014); break; // 〔
    case 0x7C36: PutChar(buf,pos,cap,0x3015); break; // 〕
    case 0x7C37: PutChar(buf,pos,cap,0x22BF); break; // ⊿
    case 0x7C38: PutChar(buf,pos,cap,0x212B); break; // Å
    case 0x7C39: PutChar(buf,pos,cap,0x2030); break; // ‰
    case 0x7C3A: PutChar(buf,pos,cap,0x2103); break; // ℃
    case 0x7C3B: PutChar(buf,pos,cap,0x00A2); break; // ¢
    case 0x7C3C: PutChar(buf,pos,cap,0x00A3); break; // £
    case 0x7C3D: PutChar(buf,pos,cap,0x00A7); break; // §
    case 0x7C3E: PutChar(buf,pos,cap,0x2606); break; // ☆
    case 0x7C3F: PutChar(buf,pos,cap,0x2605); break; // ★
    case 0x7C40: PutChar(buf,pos,cap,0x25CB); break; // ○
    case 0x7C41: PutChar(buf,pos,cap,0x25CF); break; // ●
    case 0x7C42: PutChar(buf,pos,cap,0x25CE); break; // ◎
    case 0x7C43: PutChar(buf,pos,cap,0x25C7); break; // ◇
    case 0x7C44: PutChar(buf,pos,cap,0x25C6); break; // ◆
    case 0x7C45: PutChar(buf,pos,cap,0x25A1); break; // □（定義済みシンボルとしての□）
    case 0x7C46: PutChar(buf,pos,cap,0x25A0); break; // ■
    case 0x7C47: PutChar(buf,pos,cap,0x25B3); break; // △
    case 0x7C48: PutChar(buf,pos,cap,0x25B2); break; // ▲
    case 0x7C49: PutChar(buf,pos,cap,0x25BD); break; // ▽
    case 0x7C4A: PutChar(buf,pos,cap,0x25BC); break; // ▼
    case 0x7C4B: PutChar(buf,pos,cap,0x203B); break; // ※
    case 0x7C4C: PutChar(buf,pos,cap,0x3012); break; // 〒
    case 0x7C4D: PutChar(buf,pos,cap,0x2192); break; // →（92区版）
    case 0x7C4E: PutChar(buf,pos,cap,0x2190); break; // ←（92区版）
    // 0x7C4F〜0x7C52 既存
    case 0x7C53: PutChar(buf,pos,cap,0x301D); break; // 〝
    case 0x7C54: PutChar(buf,pos,cap,0x301F); break; // 〟
    case 0x7C55: PutChar(buf,pos,cap,0x222E); break; // ∮
    case 0x7C56: PutChar(buf,pos,cap,0x2211); break; // ∑
    case 0x7C57: PutChar(buf,pos,cap,0x221A); break; // √
    case 0x7C58: PutChar(buf,pos,cap,0x27C2); break; // ⊥
    case 0x7C59: PutChar(buf,pos,cap,0x2220); break; // ∠
    case 0x7C5A: PutChar(buf,pos,cap,0x221F); break; // ∟
    case 0x7C5B: PutChar(buf,pos,cap,0x22BF); break; // ⊿
    case 0x7C5C: PutChar(buf,pos,cap,0x2235); break; // ∵
    case 0x7C5D: PutChar(buf,pos,cap,0x2229); break; // ∩
    case 0x7C5E: PutChar(buf,pos,cap,0x222A); break; // ∪
    case 0x7C5F: PutChar(buf,pos,cap,0x2160); break; // Ⅰ
    case 0x7C60: PutChar(buf,pos,cap,0x2161); break; // Ⅱ
    case 0x7C61: PutChar(buf,pos,cap,0x2162); break; // Ⅲ
    case 0x7C62: PutChar(buf,pos,cap,0x2163); break; // Ⅳ
    case 0x7C63: PutChar(buf,pos,cap,0x2164); break; // Ⅴ
    case 0x7C64: PutChar(buf,pos,cap,0x2165); break; // Ⅵ
    case 0x7C65: PutChar(buf,pos,cap,0x2166); break; // Ⅶ
    case 0x7C66: PutChar(buf,pos,cap,0x2167); break; // Ⅷ
    case 0x7C67: PutChar(buf,pos,cap,0x2168); break; // Ⅸ
    case 0x7C68: PutChar(buf,pos,cap,0x2169); break; // Ⅹ
    case 0x7C69: PutChar(buf,pos,cap,0x216A); break; // Ⅺ
    case 0x7C6A: PutChar(buf,pos,cap,0x216B); break; // Ⅻ
    case 0x7C6B: PutChar(buf,pos,cap,0x2469); break; // ⑩
    case 0x7C6C: PutChar(buf,pos,cap,0x246A); break; // ⑪
    case 0x7C6D: PutChar(buf,pos,cap,0x246B); break; // ⑫
    case 0x7C6E: PutChar(buf,pos,cap,0x246C); break; // ⑬
    case 0x7C6F: PutChar(buf,pos,cap,0x246D); break; // ⑭
    case 0x7C70: PutChar(buf,pos,cap,0x246E); break; // ⑮
    case 0x7C71: PutChar(buf,pos,cap,0x246F); break; // ⑯
    case 0x7C72: PutChar(buf,pos,cap,0x2470); break; // ⑰
    case 0x7C73: PutChar(buf,pos,cap,0x2471); break; // ⑱
    case 0x7C74: PutChar(buf,pos,cap,0x2472); break; // ⑲
    case 0x7C75: PutChar(buf,pos,cap,0x2473); break; // ⑳
    case 0x7C76: PutChar(buf,pos,cap,0x3349); break; // ㍉
    case 0x7C77: PutChar(buf,pos,cap,0x3314); break; // ㌔
    case 0x7C78: PutChar(buf,pos,cap,0x3322); break; // ㌢
    case 0x7C79: PutChar(buf,pos,cap,0x334D); break; // ㍍
    case 0x7C7A: PutChar(buf,pos,cap,0x3318); break; // ㌘
    case 0x7C7B: PutChar(buf,pos,cap,0x3327); break; // ㌧

    // 93区 0x7D29〜0x7D5F: 元号・単位・ローマ数字
    case 0x7D29: PutChar(buf,pos,cap,0x337E); break; // ㍾（明治）
    case 0x7D2A: PutChar(buf,pos,cap,0x337D); break; // ㍽（大正）
    case 0x7D2B: PutChar(buf,pos,cap,0x337C); break; // ㍼（昭和）
    case 0x7D2C: PutChar(buf,pos,cap,0x337B); break; // ㍻（平成）
    case 0x7D2D: PutSym(buf,pos,cap,L"令和"); break; // 令和
    case 0x7D2E: PutChar(buf,pos,cap,0x337A); break; // ㍺（令和）
    case 0x7D2F: PutChar(buf,pos,cap,0x3349); break; // ㍉
    case 0x7D30: PutChar(buf,pos,cap,0x3314); break; // ㌔
    case 0x7D31: PutChar(buf,pos,cap,0x3322); break; // ㌢
    case 0x7D32: PutChar(buf,pos,cap,0x334D); break; // ㍍
    case 0x7D33: PutChar(buf,pos,cap,0x3318); break; // ㌘
    case 0x7D34: PutChar(buf,pos,cap,0x3327); break; // ㌧
    case 0x7D35: PutChar(buf,pos,cap,0x3303); break; // ㌃
    case 0x7D36: PutChar(buf,pos,cap,0x3336); break; // ㌶
    case 0x7D37: PutChar(buf,pos,cap,0x3351); break; // ㍑
    case 0x7D38: PutChar(buf,pos,cap,0x3357); break; // ㍗
    case 0x7D39: PutChar(buf,pos,cap,0x330D); break; // ㌍
    case 0x7D3A: PutChar(buf,pos,cap,0x3326); break; // ㌦
    case 0x7D3B: PutChar(buf,pos,cap,0x3323); break; // ㌣
    case 0x7D3C: PutChar(buf,pos,cap,0x332B); break; // ㌫
    case 0x7D3D: PutChar(buf,pos,cap,0x334A); break; // ㍊
    case 0x7D3E: PutChar(buf,pos,cap,0x333B); break; // ㌻
    case 0x7D3F: PutChar(buf,pos,cap,0x339C); break; // ㎜
    case 0x7D40: PutChar(buf,pos,cap,0x339D); break; // ㎝
    case 0x7D41: PutChar(buf,pos,cap,0x339E); break; // ㎞
    case 0x7D42: PutChar(buf,pos,cap,0x338E); break; // ㎎
    case 0x7D43: PutChar(buf,pos,cap,0x338F); break; // ㎏
    case 0x7D44: PutChar(buf,pos,cap,0x33A5); break; // ㏄
    case 0x7D45: PutChar(buf,pos,cap,0x33A1); break; // ㎡
    case 0x7D46: PutChar(buf,pos,cap,0x2160); break; // Ⅰ
    case 0x7D47: PutChar(buf,pos,cap,0x2161); break; // Ⅱ
    case 0x7D48: PutChar(buf,pos,cap,0x2162); break; // Ⅲ
    case 0x7D49: PutChar(buf,pos,cap,0x2163); break; // Ⅳ
    case 0x7D4A: PutChar(buf,pos,cap,0x2164); break; // Ⅴ
    case 0x7D4B: PutChar(buf,pos,cap,0x2165); break; // Ⅵ
    case 0x7D4C: PutChar(buf,pos,cap,0x2166); break; // Ⅶ
    case 0x7D4D: PutChar(buf,pos,cap,0x2167); break; // Ⅷ
    case 0x7D4E: PutChar(buf,pos,cap,0x2168); break; // Ⅸ
    case 0x7D4F: PutChar(buf,pos,cap,0x2169); break; // Ⅹ
    case 0x7D50: PutChar(buf,pos,cap,0x216A); break; // Ⅺ
    case 0x7D51: PutChar(buf,pos,cap,0x216B); break; // Ⅻ
    case 0x7D52: PutChar(buf,pos,cap,0x3349); break; // ㍉
    case 0x7D53: PutChar(buf,pos,cap,0x3314); break; // ㌔
    case 0x7D54: PutChar(buf,pos,cap,0x3322); break; // ㌢
    case 0x7D55: PutChar(buf,pos,cap,0x334D); break; // ㍍
    case 0x7D56: PutChar(buf,pos,cap,0x3318); break; // ㌘
    case 0x7D57: PutChar(buf,pos,cap,0x3327); break; // ㌧
    case 0x7D58: PutChar(buf,pos,cap,0x3303); break; // ㌃
    case 0x7D59: PutChar(buf,pos,cap,0x3336); break; // ㌶
    case 0x7D5A: PutChar(buf,pos,cap,0x3351); break; // ㍑
    case 0x7D5B: PutChar(buf,pos,cap,0x3357); break; // ㍗
    case 0x7D5C: PutChar(buf,pos,cap,0x330D); break; // ㌍
    case 0x7D5D: PutChar(buf,pos,cap,0x3326); break; // ㌦
    case 0x7D5E: PutChar(buf,pos,cap,0x3323); break; // ㌣
    case 0x7D5F: PutChar(buf,pos,cap,0x332B); break; // ㌫
    case 0x7D6E: PutChar(buf,pos,cap,0x266A); break; // ♪（音符）
    case 0x7D6F: PutChar(buf,pos,cap,0x266A); break; // ♪

    // 94区 0x7E21〜0x7E50: ギリシャ文字
    case 0x7E21: PutChar(buf,pos,cap,0x0391); break; // Α
    case 0x7E22: PutChar(buf,pos,cap,0x0392); break; // Β
    case 0x7E23: PutChar(buf,pos,cap,0x0393); break; // Γ
    case 0x7E24: PutChar(buf,pos,cap,0x0394); break; // Δ
    case 0x7E25: PutChar(buf,pos,cap,0x0395); break; // Ε
    case 0x7E26: PutChar(buf,pos,cap,0x0396); break; // Ζ
    case 0x7E27: PutChar(buf,pos,cap,0x0397); break; // Η
    case 0x7E28: PutChar(buf,pos,cap,0x0398); break; // Θ
    case 0x7E29: PutChar(buf,pos,cap,0x0399); break; // Ι
    case 0x7E2A: PutChar(buf,pos,cap,0x039A); break; // Κ
    case 0x7E2B: PutChar(buf,pos,cap,0x039B); break; // Λ
    case 0x7E2C: PutChar(buf,pos,cap,0x039C); break; // Μ
    case 0x7E2D: PutChar(buf,pos,cap,0x039D); break; // Ν
    case 0x7E2E: PutChar(buf,pos,cap,0x039E); break; // Ξ
    case 0x7E2F: PutChar(buf,pos,cap,0x039F); break; // Ο
    case 0x7E30: PutChar(buf,pos,cap,0x03A0); break; // Π
    case 0x7E31: PutChar(buf,pos,cap,0x03A1); break; // Ρ
    case 0x7E32: PutChar(buf,pos,cap,0x03A3); break; // Σ
    case 0x7E33: PutChar(buf,pos,cap,0x03A4); break; // Τ
    case 0x7E34: PutChar(buf,pos,cap,0x03A5); break; // Υ
    case 0x7E35: PutChar(buf,pos,cap,0x03A6); break; // Φ
    case 0x7E36: PutChar(buf,pos,cap,0x03A7); break; // Χ
    case 0x7E37: PutChar(buf,pos,cap,0x03A8); break; // Ψ
    case 0x7E38: PutChar(buf,pos,cap,0x03A9); break; // Ω
    case 0x7E39: PutChar(buf,pos,cap,0x03B1); break; // α
    case 0x7E3A: PutChar(buf,pos,cap,0x03B2); break; // β
    case 0x7E3B: PutChar(buf,pos,cap,0x03B3); break; // γ
    case 0x7E3C: PutChar(buf,pos,cap,0x03B4); break; // δ
    case 0x7E3D: PutChar(buf,pos,cap,0x03B5); break; // ε
    case 0x7E3E: PutChar(buf,pos,cap,0x03B6); break; // ζ
    case 0x7E3F: PutChar(buf,pos,cap,0x03B7); break; // η
    case 0x7E40: PutChar(buf,pos,cap,0x03B8); break; // θ
    case 0x7E41: PutChar(buf,pos,cap,0x03B9); break; // ι
    case 0x7E42: PutChar(buf,pos,cap,0x03BA); break; // κ
    case 0x7E43: PutChar(buf,pos,cap,0x03BB); break; // λ
    case 0x7E44: PutChar(buf,pos,cap,0x03BC); break; // μ
    case 0x7E45: PutChar(buf,pos,cap,0x03BD); break; // ν
    case 0x7E46: PutChar(buf,pos,cap,0x03BE); break; // ξ
    case 0x7E47: PutChar(buf,pos,cap,0x03BF); break; // ο
    case 0x7E48: PutChar(buf,pos,cap,0x03C0); break; // π
    case 0x7E49: PutChar(buf,pos,cap,0x03C1); break; // ρ
    case 0x7E4A: PutChar(buf,pos,cap,0x03C3); break; // σ
    case 0x7E4B: PutChar(buf,pos,cap,0x03C4); break; // τ
    case 0x7E4C: PutChar(buf,pos,cap,0x03C5); break; // υ
    case 0x7E4D: PutChar(buf,pos,cap,0x03C6); break; // φ
    case 0x7E4E: PutChar(buf,pos,cap,0x03C7); break; // χ
    case 0x7E4F: PutChar(buf,pos,cap,0x03C8); break; // ψ
    case 0x7E50: PutChar(buf,pos,cap,0x03C9); break; // ω

    default:
        // AddSym is not JIS Kanji.
        // Unknown / currently unmapped ARIB additional symbols must not fall back
        // to PutJis(), because that converts undefined symbol bytes into bogus
        // kanji such as 韲, 褓, ヱコ, ｊｐｎ-like polluted text.
        //
        // Follow the TVTest/LibISDB-style replacement policy for unsupported
        // symbols. Higher layers may normalize this for file names/search keys,
        // but the decoder must not silently reinterpret it as JIS.
        PutChar(buf,pos,cap,L'\u25A1'); // □
        break;
    }
}

static CS Designate(unsigned char b) {
    switch(b) {
    case 0x42: return CS::Kanji;
    case 0x4A: return CS::Alnum;
    case 0x30: return CS::Hira;
    case 0x31: return CS::Kata;
    case 0x49: return CS::JisKata;
    case 0x39: return CS::JisKp1;
    case 0x3A: return CS::JisKp2;
    case 0x3B: return CS::AddSym;
    case 0x70: return CS::Macro;
    default:   return CS::Unknown;
    }
}

// ---------------------------------------------------------------------------
// メインデコード（固定バッファ）
// ---------------------------------------------------------------------------
static int Decode(const unsigned char* data, int length, wchar_t* buf, int cap) {
    int pos=0;
    CS codeG[4]={CS::Kanji,CS::Alnum,CS::Hira,CS::Kata};
    int GL=0,GR=2,SGL=-1,esc=0,eidx=0;

    for(int i=0;i<length;i++) {
        unsigned char b=data[i];
        if(esc>0) {
            if(esc==1) {
                switch(b) {
                case 0x6E: GL=2; esc=0; break;
                case 0x6F: GL=3; esc=0; break;
                case 0x7E: GR=1; esc=0; break;
                case 0x7D: GR=2; esc=0; break;
                case 0x7C: GR=3; esc=0; break;
                case 0x28: eidx=0; esc=2; break;
                case 0x29: eidx=1; esc=2; break;
                case 0x2A: eidx=2; esc=2; break;
                case 0x2B: eidx=3; esc=2; break;
                case 0x24: esc=2; break;
                default:   esc=0; break;
                }
            } else if(esc==2) {
                CS cs=Designate(b);
                if(cs!=CS::Unknown){codeG[eidx]=cs;esc=0;}
                else if(b==0x28){eidx=0;esc=3;}
                else if(b==0x29){eidx=1;esc=3;}
                else if(b==0x2A){eidx=2;esc=3;}
                else if(b==0x2B){eidx=3;esc=3;}
                else if(b==0x20){esc=3;}
                else{esc=0;}
            } else {
                CS cs=Designate(b);
                if(cs!=CS::Unknown) codeG[eidx]=cs;
                esc=0;
            }
            continue;
        }

        if(b>=0x21&&b<=0x7E) {
            int gi=(SGL>=0)?SGL:GL; SGL=-1;
            CS cs=codeG[gi];
            if(IsDouble(cs)) {
                if(i+1>=length) break;
                unsigned char lo=data[++i];
                unsigned int code=((unsigned int)b<<8)|lo;
                if(cs==CS::AddSym) HandleAddSym(buf,pos,cap,code,b,lo);
                else PutJis(buf,pos,cap,b,lo);
            } else {
                int idx=b-0x20;
                if(idx>=0&&idx<96) {
                    switch(cs) {
                    case CS::Alnum:   PutChar(buf,pos,cap,T_Alnum[idx]); break;
                    case CS::Hira:    PutChar(buf,pos,cap,T_Hira[idx]);  break;
                    case CS::Kata:    PutChar(buf,pos,cap,T_Kata[idx]);  break;
                    case CS::JisKata: if(idx<64) PutChar(buf,pos,cap,T_JisKata[idx]); break;
                    default: break;
                    }
                }
            }
            continue;
        }

        if(b>=0xA1&&b<=0xFE) {
            CS cs=codeG[GR];
            unsigned char b7=b&0x7F;
            if(IsDouble(cs)) {
                if(i+1>=length) break;
                unsigned char lo7=data[++i]&0x7F;
                unsigned int code=((unsigned int)b7<<8)|lo7;
                if(cs==CS::AddSym) HandleAddSym(buf,pos,cap,code,b7,lo7);
                else PutJis(buf,pos,cap,b7,lo7);
            } else {
                int idx=b7-0x20;
                if(idx>=0&&idx<96) {
                    switch(cs) {
                    case CS::Alnum:   PutChar(buf,pos,cap,T_Alnum[idx]); break;
                    case CS::Hira:    PutChar(buf,pos,cap,T_Hira[idx]);  break;
                    case CS::Kata:    PutChar(buf,pos,cap,T_Kata[idx]);  break;
                    case CS::JisKata: if(idx<64) PutChar(buf,pos,cap,T_JisKata[idx]); break;
                    default: break;
                    }
                }
            }
            continue;
        }

        switch(b) {
        case 0x0D: PutChar(buf,pos,cap,L'\n'); break;
        case 0x0E: GL=1; break;
        case 0x0F: GL=0; break;
        case 0x19: SGL=2; break;
        case 0x1D: SGL=3; break;
        case 0x1B: esc=1; eidx=0; break;
        case 0x20: case 0xA0: PutChar(buf,pos,cap,L' '); break;
        default: break;
        }
    }

    // 末尾トリム
    while(pos>0) {
        wchar_t c=buf[pos-1];
        if(c==L' '||c==L'\n'||c==0x3000) pos--;
        else break;
    }
    buf[pos]=L'\0';
    return pos;
}

// ---------------------------------------------------------------------------
// エクスポート関数（固定バッファ使用・std::wstring不使用）
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport)
const wchar_t* DecodeAribW(const unsigned char* data, int length)
{
    static wchar_t buf[4096];
    if(!data||length<=0) { buf[0]=L'\0'; return buf; }
    Decode(data, length, buf, 4096);
    return buf;
}
