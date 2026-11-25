import Foundation

/// 标准立体声 IMA-ADPCM 编解码器（48kHz 2ch，与 BoBoCore.dll / Android Kotlin / golden-vectors.json 字节级一致）。
public enum ImaAdpcmCodec {
    private static let stepTable: [Int] = [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
        34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
        157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
        724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024,
        3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
    ]

    private static let indexTable: [Int] = [
        -1, -1, -1, -1, 2, 4, 6, 8,
        -1, -1, -1, -1, 2, 4, 6, 8
    ]

    public static func encodeStereo(_ pcmInterleaved: [Int16], sampleFrames: Int? = nil) -> Data {
        let frames = sampleFrames ?? (pcmInterleaved.count / 2)
        guard frames > 0, pcmInterleaved.count >= frames * 2 else { return Data() }

        var out = Data(capacity: 8 + frames)
        var predL = Int(pcmInterleaved[0])
        var idxL = 0
        var predR = Int(pcmInterleaved[1])
        var idxR = 0

        BoBoProtocol.appendInt16LE(&out, Int16(predL))
        out.append(UInt8(idxL))
        out.append(0)
        BoBoProtocol.appendInt16LE(&out, Int16(predR))
        out.append(UInt8(idxR))
        out.append(0)

        for i in 0..<frames {
            let sL = Int(pcmInterleaved[i * 2])
            let sR = Int(pcmInterleaved[i * 2 + 1])

            var diffL = sL - predL
            var nibL = 0
            if diffL < 0 {
                nibL = 8
                diffL = -diffL
            }
            let stepL = stepTable[idxL]
            var diffqL = stepL >> 3
            if diffL >= stepL { nibL |= 4; diffL -= stepL; diffqL += stepL }
            if diffL >= (stepL >> 1) { nibL |= 2; diffL -= (stepL >> 1); diffqL += (stepL >> 1) }
            if diffL >= (stepL >> 2) { nibL |= 1; diffqL += (stepL >> 2) }
            predL = max(-32768, min(32767, (nibL & 8) != 0 ? (predL - diffqL) : (predL + diffqL)))
            idxL = max(0, min(88, idxL + indexTable[nibL]))

            var diffR = sR - predR
            var nibR = 0
            if diffR < 0 {
                nibR = 8
                diffR = -diffR
            }
            let stepR = stepTable[idxR]
            var diffqR = stepR >> 3
            if diffR >= stepR { nibR |= 4; diffR -= stepR; diffqR += stepR }
            if diffR >= (stepR >> 1) { nibR |= 2; diffR -= (stepR >> 1); diffqR += (stepR >> 1) }
            if diffR >= (stepR >> 2) { nibR |= 1; diffqR += (stepR >> 2) }
            predR = max(-32768, min(32767, (nibR & 8) != 0 ? (predR - diffqR) : (predR + diffqR)))
            idxR = max(0, min(88, idxR + indexTable[nibR]))

            out.append(UInt8((nibL & 0x0F) | ((nibR & 0x0F) << 4)))
        }
        return out
    }

    public static func decodeStereo(_ adpcm: Data, sampleFrames: Int? = nil) -> [Int16] {
        guard adpcm.count >= 8 else { return [] }
        let avail = adpcm.count - 8
        let count = min(sampleFrames ?? avail, avail)
        guard count > 0 else { return [] }

        var out = [Int16](repeating: 0, count: count * 2)
        let base = adpcm.startIndex
        var predL = Int(BoBoProtocol.readInt16LE(adpcm, offset: 0))
        var idxL = max(0, min(88, Int(adpcm[base + 2])))
        var predR = Int(BoBoProtocol.readInt16LE(adpcm, offset: 4))
        var idxR = max(0, min(88, Int(adpcm[base + 6])))

        for i in 0..<count {
            let packed = Int(adpcm[base + 8 + i])
            let nibL = packed & 0x0F
            let nibR = (packed >> 4) & 0x0F

            let stepL = stepTable[idxL]
            var diffqL = stepL >> 3
            if (nibL & 4) != 0 { diffqL += stepL }
            if (nibL & 2) != 0 { diffqL += stepL >> 1 }
            if (nibL & 1) != 0 { diffqL += stepL >> 2 }
            predL = max(-32768, min(32767, (nibL & 8) != 0 ? (predL - diffqL) : (predL + diffqL)))
            idxL = max(0, min(88, idxL + indexTable[nibL]))

            let stepR = stepTable[idxR]
            var diffqR = stepR >> 3
            if (nibR & 4) != 0 { diffqR += stepR }
            if (nibR & 2) != 0 { diffqR += stepR >> 1 }
            if (nibR & 1) != 0 { diffqR += stepR >> 2 }
            predR = max(-32768, min(32767, (nibR & 8) != 0 ? (predR - diffqR) : (predR + diffqR)))
            idxR = max(0, min(88, idxR + indexTable[nibR]))

            out[i * 2] = Int16(predL)
            out[i * 2 + 1] = Int16(predR)
        }
        return out
    }
}
