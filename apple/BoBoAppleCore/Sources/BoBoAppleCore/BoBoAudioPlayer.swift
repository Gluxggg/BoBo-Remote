import AVFoundation
import Foundation

/// 48kHz 立体声低延迟音频播放器（macOS / iOS 共享，实时解码 BoBo6 IMA-ADPCM 流）。
public final class BoBoAudioPlayer: @unchecked Sendable {
    private let lock = NSLock()
    private var engine: AVAudioEngine?
    private var playerNode: AVAudioPlayerNode?
    private var pcmFormat: AVAudioFormat?
    private var configuredRate: Int = 0
    private var _muted: Bool = false
    private var _volume: Float = 1.0

    public var muted: Bool {
        get { lock.lock(); defer { lock.unlock() }; return _muted }
        set {
            lock.lock()
            _muted = newValue
            playerNode?.volume = newValue ? 0.0 : _volume
            lock.unlock()
        }
    }

    public var volume: Float {
        get { lock.lock(); defer { lock.unlock() }; return _volume }
        set {
            lock.lock()
            _volume = max(0.0, min(1.0, newValue))
            playerNode?.volume = _muted ? 0.0 : _volume
            lock.unlock()
        }
    }

    public init() {}

    public func playAdpcm(sampleRate: Int, channels: Int, sampleCount: Int, adpcmData: Data) {
        lock.lock()
        let isMuted = _muted
        lock.unlock()
        guard !isMuted, sampleCount > 0, adpcmData.count >= 8 else { return }

        let pcm = ImaAdpcmCodec.decodeStereo(adpcmData, sampleFrames: sampleCount)
        guard !pcm.isEmpty else { return }
        let frames = pcm.count / 2
        let sr = sampleRate > 0 ? sampleRate : 48000

        lock.lock()
        ensureEngineLocked(sampleRate: sr)
        guard let fmt = pcmFormat, let node = playerNode,
              let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: AVAudioFrameCount(frames)) else {
            lock.unlock()
            return
        }
        buf.frameLength = AVAudioFrameCount(frames)
        if let chData = buf.floatChannelData {
            let left = chData[0]
            let right = chData[1]
            let inv: Float = 1.0 / 32768.0
            for i in 0..<frames {
                left[i] = Float(pcm[i * 2]) * inv
                right[i] = Float(pcm[i * 2 + 1]) * inv
            }
            node.scheduleBuffer(buf, completionHandler: nil)
            if !node.isPlaying { node.play() }
        }
        lock.unlock()
    }

    public func stop() {
        lock.lock()
        playerNode?.stop()
        engine?.stop()
        playerNode = nil
        engine = nil
        pcmFormat = nil
        configuredRate = 0
        lock.unlock()
    }

    private func ensureEngineLocked(sampleRate: Int) {
        if engine != nil && configuredRate == sampleRate { return }
        playerNode?.stop()
        engine?.stop()

        #if os(iOS)
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .moviePlayback, options: [.mixWithOthers])
        try? AVAudioSession.sharedInstance().setActive(true)
        #endif

        let eng = AVAudioEngine()
        let node = AVAudioPlayerNode()
        guard let fmt = AVAudioFormat(standardFormatWithSampleRate: Double(sampleRate), channels: 2) else { return }
        eng.attach(node)
        eng.connect(node, to: eng.mainMixerNode, format: fmt)
        node.volume = _muted ? 0.0 : _volume
        do {
            try eng.start()
            node.play()
            engine = eng
            playerNode = node
            pcmFormat = fmt
            configuredRate = sampleRate
        } catch {
            engine = nil
            playerNode = nil
        }
    }
}
