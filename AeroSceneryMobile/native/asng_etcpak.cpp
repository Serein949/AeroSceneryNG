#include <stdint.h>
#include <vector>
#include "ProcessRGB.hpp"

// etcpak's block code expects BGRA in memory for its channel packing.  ASNG works in
// RGBA, so swap the red and blue channels once per mip before handing the pixels over.
extern "C" __attribute__((visibility("default")))
void asng_etc2_encode(const uint8_t* src, int width, int height, uint8_t* dst, int heuristics)
{
    if (src == nullptr || dst == nullptr || width <= 0 || height <= 0) return;
    size_t pixels = (size_t)width * (size_t)height;
    std::vector<uint32_t> bgra(pixels);
    for (size_t i = 0; i < pixels; i++)
    {
        const uint8_t* p = src + i * 4;
        bgra[i] = (uint32_t)p[2] | ((uint32_t)p[1] << 8) | ((uint32_t)p[0] << 16) | ((uint32_t)p[3] << 24);
    }
    uint32_t blocks = (uint32_t)(pixels / 16);
    CompressEtc2Rgb(bgra.data(), reinterpret_cast<uint64_t*>(dst), blocks, (size_t)width, heuristics != 0);
}
