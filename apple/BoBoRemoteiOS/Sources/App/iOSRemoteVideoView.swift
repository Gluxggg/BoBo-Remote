import AVFoundation
import BoBoAppleCore
import SwiftUI
import UIKit

public struct iOSRemoteVideoView: UIViewRepresentable {
    public let displayLayer: AVSampleBufferDisplayLayer
    public let videoSize: CGSize
    public let touchpadMode: Bool
    public let viewOnly: Bool
    public let isAndroidHost: Bool
    public let onInput: (BoBoInputPacket) -> Void

    public func makeUIView(context: Context) -> iOSVideoCanvasUIView {
        let view = iOSVideoCanvasUIView(displayLayer: displayLayer)
        updateUIView(view, context: context)
        return view
    }

    public func updateUIView(_ uiView: iOSVideoCanvasUIView, context: Context) {
        uiView.update(videoSize: videoSize, touchpadMode: touchpadMode, viewOnly: viewOnly, isAndroidHost: isAndroidHost, onInput: onInput)
    }
}

public final class iOSVideoCanvasUIView: UIView {
    private let videoLayer: AVSampleBufferDisplayLayer
    private var videoSize = CGSize.zero
    private var touchpadMode = true
    private var viewOnly = true
    private var isAndroidHost = false
    private var onInput: ((BoBoInputPacket) -> Void)?

    private var directTouch: UITouch?
    private var directPoint: (UInt16, UInt16) = (0, 0)
    private var padTouches = Set<UITouch>()
    private var lastPadPoint = CGPoint.zero
    private var padStartPoint = CGPoint.zero
    private var padStartTime: TimeInterval = 0
    private var maxTouchCount = 0
    private var movedBeyondTap = false

    public init(displayLayer: AVSampleBufferDisplayLayer) {
        videoLayer = displayLayer
        super.init(frame: .zero)
        backgroundColor = UIColor(red: 0.055, green: 0.082, blue: 0.125, alpha: 1.0)
        isMultipleTouchEnabled = true
        videoLayer.videoGravity = .resizeAspect
        layer.addSublayer(videoLayer)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not supported") }

    public override func layoutSubviews() {
        super.layoutSubviews()
        if videoLayer.frame != bounds { cancelTouches() }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        videoLayer.frame = bounds
        CATransaction.commit()
    }

    public override func willMove(toWindow newWindow: UIWindow?) {
        if newWindow == nil { cancelTouches() }
        super.willMove(toWindow: newWindow)
    }

    func update(videoSize: CGSize, touchpadMode: Bool, viewOnly: Bool, isAndroidHost: Bool, onInput: @escaping (BoBoInputPacket) -> Void) {
        if self.videoSize != videoSize || self.touchpadMode != touchpadMode || self.viewOnly != viewOnly || self.isAndroidHost != isAndroidHost {
            cancelTouches()
        }
        self.videoSize = videoSize
        self.touchpadMode = touchpadMode
        self.viewOnly = viewOnly
        self.isAndroidHost = isAndroidHost
        self.onInput = onInput
    }

    public override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard !viewOnly, let rect = videoRect else { return }
        if touchpadMode {
            let starting = padTouches.isEmpty
            padTouches.formUnion(touches)
            lastPadPoint = padCenter
            padStartPoint = lastPadPoint
            if starting {
                padStartTime = CACurrentMediaTime()
                maxTouchCount = 0
                movedBeyondTap = false
            }
            maxTouchCount = max(maxTouchCount, padTouches.count)
        } else if directTouch == nil, let touch = touches.first(where: { rect.contains($0.location(in: self)) }) {
            directTouch = touch
            directPoint = normalize(touch.location(in: self), in: rect)
            sendDirectTouch(action: BoBoProtocol.touchActionDown)
        }
    }

    public override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        if touchpadMode {
            guard !padTouches.isDisjoint(with: touches) else { return }
            let point = padCenter
            let dx = point.x - lastPadPoint.x
            let dy = point.y - lastPadPoint.y
            lastPadPoint = point
            if abs(point.x - padStartPoint.x) > 10 || abs(point.y - padStartPoint.y) > 10 { movedBeyondTap = true }
            if padTouches.count >= 2 {
                let scroll = Int((dy * 3).rounded())
                if scroll != 0 { onInput?(BoBoProtocol.buildMouseWheel(delta: scroll, horizontal: false)) }
            } else {
                let dx = Int((dx * 1.8).rounded())
                let dy = Int((dy * 1.8).rounded())
                if dx != 0 || dy != 0 { onInput?(BoBoProtocol.buildMouseRel(dx: dx, dy: dy)) }
            }
        } else if let directTouch, touches.contains(directTouch), let rect = videoRect {
            directPoint = normalize(directTouch.location(in: self), in: rect)
            sendDirectTouch(action: BoBoProtocol.touchActionMove)
        }
    }

    public override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        if touchpadMode {
            guard !padTouches.isDisjoint(with: touches) else { return }
            padTouches.subtract(touches)
            if padTouches.isEmpty {
                if !movedBeyondTap, CACurrentMediaTime() - padStartTime < 0.28 {
                    let button: Int16 = maxTouchCount >= 2 ? 3 : 1
                    onInput?(BoBoProtocol.buildMouseButton(button: button, down: true))
                    onInput?(BoBoProtocol.buildMouseButton(button: button, down: false))
                }
            } else {
                lastPadPoint = padCenter
                padStartPoint = lastPadPoint
            }
        } else if let directTouch, touches.contains(directTouch) {
            if let rect = videoRect { directPoint = normalize(directTouch.location(in: self), in: rect) }
            sendDirectTouch(action: BoBoProtocol.touchActionUp)
            self.directTouch = nil
        }
    }

    public override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        if !padTouches.isDisjoint(with: touches) || directTouch.map({ touches.contains($0) }) == true {
            cancelTouches()
        }
    }

    private func cancelTouches() {
        if directTouch != nil { sendDirectTouch(action: BoBoProtocol.touchActionCancel) }
        directTouch = nil
        padTouches.removeAll()
    }

    private func sendDirectTouch(action: UInt8) {
        let (x, y) = directPoint
        if isAndroidHost {
            onInput?(BoBoProtocol.buildTouch(pointerId: 0, action: action, nx: x, ny: y))
        } else {
            if action != BoBoProtocol.touchActionCancel { onInput?(BoBoProtocol.buildMouseAbs(nx: x, ny: y)) }
            if action != BoBoProtocol.touchActionMove {
                onInput?(BoBoProtocol.buildMouseButton(button: 1, down: action == BoBoProtocol.touchActionDown))
            }
        }
    }

    private var videoRect: CGRect? {
        guard videoSize.width > 0, videoSize.height > 0, bounds.width > 1, bounds.height > 1 else { return nil }
        return AVMakeRect(aspectRatio: videoSize, insideRect: bounds)
    }

    private var padCenter: CGPoint {
        let total = padTouches.reduce(CGPoint.zero) { point, touch in
            let next = touch.location(in: self)
            return CGPoint(x: point.x + next.x, y: point.y + next.y)
        }
        let count = CGFloat(padTouches.count)
        return CGPoint(x: total.x / count, y: total.y / count)
    }

    private func normalize(_ point: CGPoint, in rect: CGRect) -> (UInt16, UInt16) {
        let x = max(0, min(1, (point.x - rect.minX) / rect.width))
        let y = max(0, min(1, (point.y - rect.minY) / rect.height))
        return (UInt16((x * 65535).rounded()), UInt16((y * 65535).rounded()))
    }
}
