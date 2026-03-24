// JavaRandomGPU.cginc — Bit-exact port of java.util.Random's 48-bit LCG to HLSL.
// Uses uint2(hi16, lo32) for the 48-bit seed. hi stores bits 47..32, lo stores bits 31..0.
//
// LCG: seed = (seed * 0x5DEECE66D + 0xB) & ((1<<48)-1)
// Multiplier 25214903917 = 0x0005_DEECE66D → uint2(0x5, 0xDEECE66D)
// Addend 11 = uint2(0x0, 0xB)

#ifndef JAVA_RANDOM_GPU_INCLUDED
#define JAVA_RANDOM_GPU_INCLUDED

// Multiply two 48-bit numbers as uint2(hi16, lo32), return low 48 bits.
uint2 _jrMul48(uint2 a, uint2 b)
{
    uint a_lo = a.y & 0xFFFFu;
    uint a_hi = a.y >> 16u;
    uint b_lo = b.y & 0xFFFFu;
    uint b_hi = b.y >> 16u;

    uint p0 = a_lo * b_lo;
    uint p1 = a_lo * b_hi;
    uint p2 = a_hi * b_lo;
    uint p3 = a_hi * b_hi;

    uint mid = (p0 >> 16u) + (p1 & 0xFFFFu) + (p2 & 0xFFFFu);
    uint lo = (p0 & 0xFFFFu) | ((mid & 0xFFFFu) << 16u);
    uint hi = (mid >> 16u) + (p1 >> 16u) + (p2 >> 16u) + p3;

    // Cross terms (only low 16 bits of hi matter)
    hi += a.y * b.x + a.x * b.y;
    hi &= 0xFFFFu;
    return uint2(hi, lo);
}

uint2 _jrAdd48(uint2 a, uint2 b)
{
    uint lo = a.y + b.y;
    uint carry = (lo < a.y) ? 1u : 0u;
    uint hi = (a.x + b.x + carry) & 0xFFFFu;
    return uint2(hi, lo);
}

static const uint2 JR_MULTIPLIER = uint2(0x5u, 0xDEECE66Du);
static const uint2 JR_ADDEND = uint2(0u, 0xBu);

uint2 jrStep(uint2 state)
{
    return _jrAdd48(_jrMul48(state, JR_MULTIPLIER), JR_ADDEND);
}

// setSeed(long): seed = (seedVal ^ multiplier) & mask48
uint2 jrSetSeed(int seedHi, uint seedLo)
{
    uint2 s = uint2((uint)seedHi ^ JR_MULTIPLIER.x, seedLo ^ JR_MULTIPLIER.y);
    s.x &= 0xFFFFu;
    return s;
}

// next(bits): advance state, return top `bits` bits of the 48-bit seed
// Java: return (int)(seed >>> (48 - bits))
int jrNext(inout uint2 state, int bits)
{
    state = jrStep(state);
    int shift = 48 - bits;
    uint result;
    if (shift >= 32)
        result = state.x >> (uint)(shift - 32);
    else if (shift > 0)
        result = (state.x << (uint)(32 - shift)) | (state.y >> (uint)shift);
    else
        result = state.y;
    // Avoid UB: (1u << 32u) is undefined. For bits=32, no mask needed.
    if (bits < 32) result &= (1u << (uint)bits) - 1u;
    return (int)result;
}

int jrNextInt(inout uint2 state) { return jrNext(state, 32); }

// nextInt(bound): exact Java rejection sampling
int jrNextIntBound(inout uint2 state, int bound)
{
    if ((bound & -bound) == bound)
    {
        // bound is 2^k. Java: (int)((bound * (long)next(31)) >> 31) = next(31) >> (31-k)
        int r = jrNext(state, 31);
        int k = firstbitlow((uint)bound);
        return r >> (31 - k);
    }
    int bits, val;
    [loop] for (int i = 0; i < 64; i++)
    {
        bits = jrNext(state, 31);
        val = bits % bound;
        if (bits - val + (bound - 1) >= 0) return val;
    }
    return val;
}

float jrNextFloat(inout uint2 state)
{
    return (float)jrNext(state, 24) / 16777216.0;
}

int2 jrNextLong(inout uint2 state)
{
    int hi = jrNext(state, 32);
    int lo = jrNext(state, 32);
    return int2(hi, lo);
}

// Seed a new RNG from this RNG's nextLong
uint2 jrSetSeedFromNextLong(inout uint2 state)
{
    int2 lng = jrNextLong(state);
    return jrSetSeed(lng.x, (uint)lng.y);
}

// === 64-bit integer helpers for MapGenBase seed calc ===

int2 _jr64Mul(int2 a, int2 b)
{
    uint a_lo = (uint)a.y & 0xFFFFu;
    uint a_hi = (uint)a.y >> 16u;
    uint b_lo = (uint)b.y & 0xFFFFu;
    uint b_hi = (uint)b.y >> 16u;

    uint p0 = a_lo * b_lo;
    uint p1 = a_lo * b_hi;
    uint p2 = a_hi * b_lo;
    uint p3 = a_hi * b_hi;

    uint mid = (p0 >> 16u) + (p1 & 0xFFFFu) + (p2 & 0xFFFFu);
    uint lo = (p0 & 0xFFFFu) | ((mid & 0xFFFFu) << 16u);
    uint hi = (mid >> 16u) + (p1 >> 16u) + (p2 >> 16u) + p3;
    hi += (uint)a.y * (uint)b.x + (uint)a.x * (uint)b.y;

    return int2((int)hi, (int)lo);
}

int2 _jr64Add(int2 a, int2 b)
{
    uint lo = (uint)a.y + (uint)b.y;
    uint carry = (lo < (uint)a.y) ? 1u : 0u;
    uint hi = (uint)a.x + (uint)b.x + carry;
    return int2((int)hi, (int)lo);
}

int2 _jr64Xor(int2 a, int2 b)
{
    return int2(a.x ^ b.x, a.y ^ b.y);
}

#endif
