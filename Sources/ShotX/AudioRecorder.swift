import Cocoa
import AVFoundation
import SwiftUI

enum AudioRecordState {
    case ready
    case recording
    case paused
}

final class AudioRecorderController: NSObject, ObservableObject, AVAudioRecorderDelegate {
    static let shared = AudioRecorderController()

    @Published private(set) var state: AudioRecordState = .ready
    @Published private(set) var isRecording = false
    @Published private(set) var duration: TimeInterval = 0

    private var recorder: AVAudioRecorder?
    private var timer: Timer?
    private var currentFileURL: URL?
    private var floatingPanel: NSPanel?

    func toggleRecording() {
        if floatingPanel != nil {
            if state == .recording {
                pauseRecording()
            } else if state == .paused {
                resumeRecording()
            } else {
                dismissFloatingPill()
            }
        } else {
            presentRecorder()
        }
    }

    /// 打开声音录制悬浮窗（不自动开始录音，等待用户自主点击“开始”）
    func presentRecorder() {
        if let panel = floatingPanel {
            panel.orderFrontRegardless()
            return
        }
        self.state = .ready
        self.isRecording = false
        self.duration = 0
        showFloatingPill()
    }

    func startRecording() {
        guard state == .ready else { return }

        if #available(macOS 10.14, *) {
            let status = AVCaptureDevice.authorizationStatus(for: .audio)
            if status == .notDetermined {
                AVCaptureDevice.requestAccess(for: .audio) { [weak self] granted in
                    DispatchQueue.main.async {
                        if granted {
                            self?.startRecordingInternal()
                        } else {
                            self?.showMicDeniedAlert()
                        }
                    }
                }
                return
            } else if status == .denied || status == .restricted {
                showMicDeniedAlert()
                return
            }
        }

        startRecordingInternal()
    }

    private func startRecordingInternal() {
        let folder = PreferencesStore.shared.audioRecordingSaveDirectoryURL
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd HH.mm.ss"
        let filename = "ShotX 录音 \(formatter.string(from: Date())).m4a"
        let fileURL = folder.appendingPathComponent(filename)
        currentFileURL = fileURL

        let settings: [String: Any] = [
            AVFormatIDKey: Int(kAudioFormatMPEG4AAC),
            AVSampleRateKey: 44100.0,
            AVNumberOfChannelsKey: 2,
            AVEncoderAudioQualityKey: AVAudioQuality.high.rawValue
        ]

        do {
            let rec = try AVAudioRecorder(url: fileURL, settings: settings)
            rec.delegate = self
            rec.record()
            self.recorder = rec
            self.state = .recording
            self.isRecording = true
            self.duration = 0

            startTimer()

            ToastController.shared.show(
                message: "正在录音中…",
                icon: "mic.fill",
                tint: .systemRed,
                duration: 2.0,
                position: .center
            )
        } catch {
            ToastController.shared.show(
                message: "启动录音失败：\(error.localizedDescription)",
                icon: "exclamationmark.triangle.fill",
                tint: .systemRed,
                position: .center
            )
        }
    }

    func pauseRecording() {
        guard state == .recording else { return }
        recorder?.pause()
        stopTimer()
        state = .paused
        isRecording = false
    }

    func resumeRecording() {
        guard state == .paused else { return }
        recorder?.record()
        startTimer()
        state = .recording
        isRecording = true
    }

    func cancelRecorder() {
        stopTimer()
        recorder?.stop()
        recorder = nil
        isRecording = false
        state = .ready
        if let fileURL = currentFileURL, FileManager.default.fileExists(atPath: fileURL.path) {
            try? FileManager.default.removeItem(at: fileURL)
        }
        currentFileURL = nil
        dismissFloatingPill()
    }

    func stopRecording() {
        guard state == .recording || state == .paused else {
            dismissFloatingPill()
            return
        }

        stopTimer()
        recorder?.stop()
        recorder = nil
        isRecording = false
        state = .ready
        dismissFloatingPill()

        if let fileURL = currentFileURL, FileManager.default.fileExists(atPath: fileURL.path) {
            SoundEffect.shared.playShutter()
            HistoryStore.shared.addAudio(url: fileURL, duration: self.duration)
            NSWorkspace.shared.activateFileViewerSelecting([fileURL])
            ToastController.shared.show(
                message: "录音已保存至: \(fileURL.lastPathComponent)",
                icon: "checkmark.circle.fill",
                tint: .systemGreen,
                duration: 3.5,
                position: .center
            )
        }
    }

    private func startTimer() {
        stopTimer()
        timer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            guard let self = self else { return }
            self.duration += 1.0
        }
    }

    private func stopTimer() {
        timer?.invalidate()
        timer = nil
    }

    private func showMicDeniedAlert() {
        let alert = NSAlert()
        alert.messageText = "需要麦克风访问权限"
        alert.informativeText = "请在“系统设置 → 隐私与安全性 → 麦克风”中允许 ShotX 访问麦克风以进行录音。"
        alert.alertStyle = .warning
        alert.addButton(withTitle: "打开系统设置")
        alert.addButton(withTitle: "取消")
        if alert.runModal() == .alertFirstButtonReturn {
            if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone") {
                NSWorkspace.shared.open(url)
            }
        }
    }

    // MARK: - Floating Pill Overlay

    private func showFloatingPill() {
        dismissFloatingPill()

        let pillView = AudioRecordingPillView(recorder: self)
        let hosting = NSHostingController(rootView: pillView)
        let pillSize = NSSize(width: 255, height: 44)
        hosting.view.frame = NSRect(origin: .zero, size: pillSize)

        let panel = NSPanel(
            contentRect: NSRect(origin: .zero, size: pillSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.isFloatingPanel = true
        panel.level = .statusBar
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.contentViewController = hosting
        panel.isMovableByWindowBackground = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.isReleasedWhenClosed = false

        if let screen = NSScreen.main {
            let x = screen.visibleFrame.maxX - pillSize.width - 24
            let y = screen.visibleFrame.maxY - pillSize.height - 24
            panel.setFrame(NSRect(origin: NSPoint(x: x, y: y), size: pillSize), display: true)
        }

        panel.orderFrontRegardless()
        CaptureExclusions.register(panel)
        self.floatingPanel = panel
    }

    private func dismissFloatingPill() {
        guard let p = floatingPanel else { return }
        CaptureExclusions.unregister(p)
        p.orderOut(nil)
        p.contentView = nil
        p.close()
        floatingPanel = nil
    }
}

private struct AudioRecordingPillView: View {
    @ObservedObject var recorder: AudioRecorderController

    var body: some View {
        HStack(spacing: 8) {
            // 状态指示圆点
            Group {
                switch recorder.state {
                case .ready:
                    Circle()
                        .fill(Color.gray)
                        .frame(width: 9, height: 9)
                case .recording:
                    Circle()
                        .fill(Color.red)
                        .frame(width: 9, height: 9)
                        .overlay(
                            Circle()
                                .stroke(Color.white.opacity(0.8), lineWidth: 1.5)
                        )
                case .paused:
                    Circle()
                        .fill(Color.orange)
                        .frame(width: 9, height: 9)
                }
            }

            // 计时显示
            Text(formatDuration(recorder.duration))
                .font(.system(size: 13, weight: .bold, design: .monospaced))
                .foregroundStyle(.white)

            Spacer(minLength: 4)

            // 操作控制按钮组 (开始、暂停/继续、停止)
            if recorder.state == .ready {
                Button(action: { recorder.startRecording() }) {
                    HStack(spacing: 4) {
                        Image(systemName: "record.circle.fill")
                            .font(.system(size: 11, weight: .bold))
                        Text("开始")
                            .font(.system(size: 11, weight: .semibold))
                    }
                    .padding(.horizontal, 9)
                    .padding(.vertical, 4)
                    .background(Color.red.opacity(0.9))
                    .clipShape(Capsule())
                }
                .buttonStyle(.plain)
                .foregroundStyle(.white)

                Button(action: { recorder.cancelRecorder() }) {
                    Image(systemName: "xmark")
                        .font(.system(size: 10, weight: .semibold))
                        .padding(5)
                        .background(Color.white.opacity(0.15))
                        .clipShape(Circle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(.white)
            } else {
                // 暂停 / 继续
                Button(action: {
                    if recorder.state == .recording {
                        recorder.pauseRecording()
                    } else {
                        recorder.resumeRecording()
                    }
                }) {
                    HStack(spacing: 3) {
                        Image(systemName: recorder.state == .recording ? "pause.fill" : "play.fill")
                            .font(.system(size: 9, weight: .bold))
                        Text(recorder.state == .recording ? "暂停" : "继续")
                            .font(.system(size: 11, weight: .semibold))
                    }
                    .padding(.horizontal, 8)
                    .padding(.vertical, 4)
                    .background(Color.white.opacity(0.2))
                    .clipShape(Capsule())
                }
                .buttonStyle(.plain)
                .foregroundStyle(.white)

                // 停止并保存
                Button(action: { recorder.stopRecording() }) {
                    HStack(spacing: 3) {
                        Image(systemName: "stop.fill")
                            .font(.system(size: 9, weight: .bold))
                        Text("停止")
                            .font(.system(size: 11, weight: .semibold))
                    }
                    .padding(.horizontal, 8)
                    .padding(.vertical, 4)
                    .background(Color.red.opacity(0.85))
                    .clipShape(Capsule())
                }
                .buttonStyle(.plain)
                .foregroundStyle(.white)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(Color.black.opacity(0.85))
        .clipShape(Capsule())
        .overlay(
            Capsule().strokeBorder(Color.white.opacity(0.18), lineWidth: 1)
        )
        .shadow(color: .black.opacity(0.4), radius: 8, y: 3)
    }

    private func formatDuration(_ t: TimeInterval) -> String {
        let total = Int(t)
        let m = total / 60
        let s = total % 60
        return String(format: "%02d:%02d", m, s)
    }
}
