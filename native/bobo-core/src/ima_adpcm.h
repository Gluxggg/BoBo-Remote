#pragma once

#include <cstdint>
#include <vector>

namespace bobo {

// Compact stereo/mono IMA-ADPCM codec (4:1 compression, ~384 kbps @ 48kHz stereo, zero external DLLs).
// Packet layout: per channel [int16 predictor][uint8 stepIndex][uint8 reserved], followed by packed 4-bit nibbles.
class ImaAdpcmCodec {
public:
    static void EncodeStereo(const int16_t* pcmInterleaved, int sampleFrames, std::vector<uint8_t>& outPacket) {
        outPacket.clear();
        if (!pcmInterleaved || sampleFrames <= 0) return;

        int pred[2] = { pcmInterleaved[0], pcmInterleaved[1] };
        int idx[2] = { 0, 0 };

        // 8-byte header (4 bytes per channel) + sampleFrames bytes (2 nibbles = 1 byte per stereo frame)
        outPacket.resize(8 + static_cast<size_t>(sampleFrames));
        outPacket[0] = static_cast<uint8_t>(pred[0] & 0xFF);
        outPacket[1] = static_cast<uint8_t>((pred[0] >> 8) & 0xFF);
        outPacket[2] = static_cast<uint8_t>(idx[0]);
        outPacket[3] = 0;
        outPacket[4] = static_cast<uint8_t>(pred[1] & 0xFF);
        outPacket[5] = static_cast<uint8_t>((pred[1] >> 8) & 0xFF);
        outPacket[6] = static_cast<uint8_t>(idx[1]);
        outPacket[7] = 0;

        for (int s = 0; s < sampleFrames; ++s) {
            uint8_t nL = EncodeSample(pcmInterleaved[s * 2], pred[0], idx[0]);
            uint8_t nR = EncodeSample(pcmInterleaved[s * 2 + 1], pred[1], idx[1]);
            outPacket[8 + static_cast<size_t>(s)] = static_cast<uint8_t>((nL & 0x0F) | ((nR & 0x0F) << 4));
        }
    }

    static bool DecodeStereo(const uint8_t* packet, int packetLen, std::vector<int16_t>& outPcm) {
        outPcm.clear();
        if (!packet || packetLen < 9) return false;

        int pred[2];
        pred[0] = static_cast<int16_t>(static_cast<uint16_t>(packet[0]) | (static_cast<uint16_t>(packet[1]) << 8));
        pred[1] = static_cast<int16_t>(static_cast<uint16_t>(packet[4]) | (static_cast<uint16_t>(packet[5]) << 8));
        int idx[2] = { packet[2], packet[6] };
        if (idx[0] > 88) idx[0] = 88;
        if (idx[1] > 88) idx[1] = 88;

        int frames = packetLen - 8;
        outPcm.resize(static_cast<size_t>(frames) * 2);
        for (int s = 0; s < frames; ++s) {
            uint8_t b = packet[8 + s];
            uint8_t nL = b & 0x0F;
            uint8_t nR = (b >> 4) & 0x0F;
            outPcm[static_cast<size_t>(s) * 2] = DecodeSample(nL, pred[0], idx[0]);
            outPcm[static_cast<size_t>(s) * 2 + 1] = DecodeSample(nR, pred[1], idx[1]);
        }
        return true;
    }

private:
    static const int* StepTable() {
        static const int kStepTable[89] = {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
            34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
            157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
            724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024,
            3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
            15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
        };
        return kStepTable;
    }

    static const int* IndexTable() {
        static const int kIndexTable[16] = {
            -1, -1, -1, -1, 2, 4, 6, 8,
            -1, -1, -1, -1, 2, 4, 6, 8
        };
        return kIndexTable;
    }

    static uint8_t EncodeSample(int16_t sample, int& predictor, int& index) {
        const int* steps = StepTable();
        const int* indices = IndexTable();
        int step = steps[index];
        int diff = static_cast<int>(sample) - predictor;
        uint8_t nibble = 0;
        if (diff < 0) {
            nibble = 8;
            diff = -diff;
        }
        int delta = step >> 3;
        if (diff >= step) { nibble |= 4; diff -= step; delta += step; }
        step >>= 1;
        if (diff >= step) { nibble |= 2; diff -= step; delta += step; }
        step >>= 1;
        if (diff >= step) { nibble |= 1; delta += step; }

        if (nibble & 8) predictor -= delta;
        else predictor += delta;
        if (predictor > 32767) predictor = 32767;
        if (predictor < -32768) predictor = -32768;

        index += indices[nibble];
        if (index < 0) index = 0;
        if (index > 88) index = 88;
        return nibble;
    }

    static int16_t DecodeSample(uint8_t nibble, int& predictor, int& index) {
        const int* steps = StepTable();
        const int* indices = IndexTable();
        int step = steps[index];
        int delta = step >> 3;
        if (nibble & 4) delta += step;
        if (nibble & 2) delta += (step >> 1);
        if (nibble & 1) delta += (step >> 2);

        if (nibble & 8) predictor -= delta;
        else predictor += delta;
        if (predictor > 32767) predictor = 32767;
        if (predictor < -32768) predictor = -32768;

        index += indices[nibble & 0x0F];
        if (index < 0) index = 0;
        if (index > 88) index = 88;
        return static_cast<int16_t>(predictor);
    }
};

} // namespace bobo
