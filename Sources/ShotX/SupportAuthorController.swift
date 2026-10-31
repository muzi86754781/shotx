import Cocoa
import SwiftUI

final class SupportAuthorController {
    static let shared = SupportAuthorController()

    private var window: NSWindow?

    func show() {
        if let w = window {
            w.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }

        let view = SupportAuthorView { [weak self] in
            self?.window?.close()
            self?.window = nil
        }

        let hosting = NSHostingController(rootView: view)
        let w = NSWindow(contentViewController: hosting)
        w.title = "支持作者 · ShotX"
        w.styleMask = [.titled, .closable]
        w.setContentSize(NSSize(width: 480, height: 640))
        w.isReleasedWhenClosed = false
        w.center()
        w.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        self.window = w
    }
}

struct SupportAuthorView: View {
    let onClose: () -> Void

    @State private var qrImage: NSImage? = nil

    var body: some View {
        ScrollView {
            VStack(spacing: 18) {
                // Header
                VStack(spacing: 8) {
                    ZStack {
                        Circle()
                            .fill(
                                LinearGradient(
                                    colors: [Color.orange.opacity(0.2), Color.pink.opacity(0.2)],
                                    startPoint: .topLeading,
                                    endPoint: .bottomTrailing
                                )
                            )
                            .frame(width: 54, height: 54)
                        Image(systemName: "cup.and.saucer.fill")
                            .font(.system(size: 26))
                            .foregroundStyle(Color.orange)
                    }

                    Text("支持与赞赏作者")
                        .font(.system(size: 19, weight: .bold))

                    Text("独立开发不易 · 感恩一路相伴同行")
                        .font(.system(size: 12.5))
                        .foregroundStyle(.secondary)
                }
                .padding(.top, 14)

                // Story Card
                VStack(alignment: .leading, spacing: 10) {
                    HStack(spacing: 6) {
                        Image(systemName: "quote.opening")
                            .font(.system(size: 12))
                            .foregroundStyle(Color.accentColor)
                        Text("作者心声")
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(Color.accentColor)
                    }

                    Text("ShotX 是一款追求极致体验的独立原生 macOS 生产力工具。\n\n为了给您带来流畅丝滑、无广告打扰且小巧高效的使用体验，从全能截图、就地丰富标注、滚动长截图实时拼接、离线 OCR，到划词/截图/输入翻译、剪贴板历史与屏幕录制，作者利用业余时间持续投入了大量的心血与精力进行设计开发与反复打磨。\n\n您的理解、支持与鼓励，是 ShotX 保持高频更新与不断精进的最大动力！如果您觉得 ShotX 为您的日常学习和工作带来了哪怕一点点便利，欢迎请作者喝杯咖啡 ☕️，由衷感谢每一位温暖的同行者！")
                        .font(.system(size: 12.5))
                        .lineSpacing(4)
                        .foregroundStyle(.primary)

                    Divider().padding(.vertical, 2)

                    // QQ Contact info
                    HStack {
                        Image(systemName: "message.fill")
                            .font(.system(size: 12))
                            .foregroundStyle(.secondary)
                        Text("作者 QQ：")
                            .font(.system(size: 12.5, weight: .medium))
                        Text("285351298")
                            .font(.system(size: 13, weight: .bold, design: .monospaced))
                            .foregroundStyle(Color.accentColor)

                        Spacer()

                        Button {
                            let pb = NSPasteboard.general
                            pb.clearContents()
                            pb.setString("285351298", forType: .string)
                            ToastController.shared.show(
                                message: "已复制作者 QQ：285351298",
                                icon: "checkmark.circle.fill",
                                tint: .systemGreen,
                                duration: 2.0,
                                position: .center
                            )
                        } label: {
                            Label("复制 QQ", systemImage: "doc.on.doc")
                                .font(.system(size: 11))
                        }
                        .buttonStyle(.bordered)
                        .controlSize(.small)
                    }
                }
                .padding(14)
                .background(
                    RoundedRectangle(cornerRadius: 12)
                        .fill(Color(NSColor.controlBackgroundColor))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 12)
                        .strokeBorder(Color.primary.opacity(0.08), lineWidth: 1)
                )

                // QR Code Card
                VStack(spacing: 12) {
                    Text("微信 / 支付宝 赞赏码")
                        .font(.system(size: 13, weight: .semibold))

                    if let img = qrImage {
                        Image(nsImage: img)
                            .resizable()
                            .aspectRatio(contentMode: .fit)
                            .frame(width: 220, height: 220)
                            .clipShape(RoundedRectangle(cornerRadius: 12))
                            .overlay(
                                RoundedRectangle(cornerRadius: 12)
                                    .strokeBorder(Color.primary.opacity(0.12), lineWidth: 1)
                            )
                            .shadow(color: .black.opacity(0.08), radius: 6, y: 3)
                    } else {
                        Rectangle()
                            .fill(Color.secondary.opacity(0.1))
                            .frame(width: 220, height: 220)
                            .overlay(Text("加载收款码中…").font(.system(size: 12)).foregroundStyle(.secondary))
                            .clipShape(RoundedRectangle(cornerRadius: 12))
                    }

                    HStack(spacing: 12) {
                        Button {
                            saveQRCodeImage()
                        } label: {
                            Label("保存收款码到下载", systemImage: "arrow.down.circle")
                                .font(.system(size: 12))
                        }
                        .buttonStyle(.bordered)
                        .controlSize(.small)

                        Button {
                            onClose()
                        } label: {
                            Text("关闭")
                                .font(.system(size: 12))
                                .frame(width: 60)
                        }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.small)
                    }
                }
                .padding(14)
                .frame(maxWidth: .infinity)
                .background(
                    RoundedRectangle(cornerRadius: 12)
                        .fill(Color(NSColor.controlBackgroundColor))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 12)
                        .strokeBorder(Color.primary.opacity(0.08), lineWidth: 1)
                )
            }
            .padding(20)
        }
        .frame(minWidth: 460, minHeight: 600)
        .background(Color(NSColor.windowBackgroundColor))
        .onAppear {
            loadQRCode()
        }
    }

    private func loadQRCode() {
        if let url = Bundle.main.url(forResource: "shou", withExtension: "jpg"),
           let img = NSImage(contentsOf: url) {
            qrImage = img
            return
        }

        // Fallback: search relative resources path
        let candidates = [
            "Resources/shou.jpg",
            "../Resources/shou.jpg",
            Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/shou.jpg").path
        ]
        for path in candidates {
            if FileManager.default.fileExists(atPath: path),
               let img = NSImage(contentsOfFile: path) {
                qrImage = img
                return
            }
        }
    }

    private func saveQRCodeImage() {
        guard let img = qrImage,
              let tiff = img.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff),
              let jpg = rep.representation(using: .jpeg, properties: [:]) else {
            return
        }

        if let downloads = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first {
            let dest = downloads.appendingPathComponent("ShotX_作者收款码.jpg")
            try? jpg.write(to: dest)
            NSWorkspace.shared.activateFileViewerSelecting([dest])
            ToastController.shared.show(
                message: "收款码已保存至下载文件夹",
                icon: "checkmark.circle.fill",
                tint: .systemGreen,
                duration: 2.0,
                position: .center
            )
        }
    }
}
