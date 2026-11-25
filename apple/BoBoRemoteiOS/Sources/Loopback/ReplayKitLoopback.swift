enum ReplayKitLoopback {
    static let port: UInt16 = 49811
    static let maxSide = 1280
    static let framesPerSecond = 30
    static let bitrateKbps = 4000
    static let maxVideoPayloadBytes = 2 * 1024 * 1024

    // The app acknowledges each complete video frame with one byte. It can also
    // request an IDR in that byte, so control messages cannot queue behind video.
    static let acknowledge: UInt8 = 1
    static let requestKeyframe: UInt8 = 2
}
