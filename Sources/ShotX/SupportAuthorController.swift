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
        w.setContentSize(NSSize(width: 540, height: 450))
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
        VStack(spacing: 16) {
            // Header
            HStack(spacing: 12) {
                ZStack {
                    Circle()
                        .fill(
                            LinearGradient(
                                colors: [Color.orange.opacity(0.2), Color.pink.opacity(0.2)],
                                startPoint: .topLeading,
                                endPoint: .bottomTrailing
                            )
                        )
                        .frame(width: 44, height: 44)
                    Image(systemName: "cup.and.saucer.fill")
                        .font(.system(size: 22))
                        .foregroundStyle(Color.orange)
                }

                VStack(alignment: .leading, spacing: 2) {
                    Text("支持与赞赏作者")
                        .font(.system(size: 17, weight: .bold))
                    Text("ShotX 独立开发不易 · 感恩一路温暖相伴")
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                }

                Spacer()
            }
            .padding(.horizontal, 20)
            .padding(.top, 16)

            // Content Area: Left Story/Info + Right WeChat QR Code (No ScrollView, zero scrollbars!)
            HStack(alignment: .top, spacing: 18) {
                // Left Column: Developer Story & QQ
                VStack(alignment: .leading, spacing: 12) {
                    VStack(alignment: .leading, spacing: 8) {
                        HStack(spacing: 6) {
                            Image(systemName: "quote.opening")
                                .font(.system(size: 11))
                                .foregroundStyle(Color.accentColor)
                            Text("作者心声")
                                .font(.system(size: 12.5, weight: .semibold))
                                .foregroundStyle(Color.accentColor)
                        }

                        Text("ShotX 追求丝滑、小巧、纯净与高效。从全能截图、几何标注、滚动长截图实时拼接、离线 OCR，到划词/截图/输入多模态翻译、剪贴板历史与屏幕录制，无数个夜晚与周末都在代码调试和体验打磨中度过。\n\nShotX 坚持永久纯净、无任何广告。您的认可与赞赏，是作者持续迭代与维护的最大动力！如果 ShotX 为您的日常工作带来了哪怕一点点便利，欢迎请作者喝杯咖啡 ☕️！")
                            .font(.system(size: 11.5))
                            .lineSpacing(3.5)
                            .foregroundStyle(.secondary)
                    }
                    .padding(12)
                    .background(
                        RoundedRectangle(cornerRadius: 10)
                            .fill(Color(NSColor.controlBackgroundColor))
                    )
                    .overlay(
                        RoundedRectangle(cornerRadius: 10)
                            .strokeBorder(Color.primary.opacity(0.06), lineWidth: 1)
                    )

                    // QQ Row
                    HStack(spacing: 8) {
                        Image(systemName: "message.fill")
                            .font(.system(size: 12))
                            .foregroundStyle(.secondary)
                        Text("作者 QQ:")
                            .font(.system(size: 12, weight: .medium))
                        Text("285351298")
                            .font(.system(size: 12.5, weight: .bold, design: .monospaced))
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
                                duration: 2.0
                            )
                        } label: {
                            Label("复制", systemImage: "doc.on.doc")
                                .font(.system(size: 11))
                        }
                        .buttonStyle(.bordered)
                        .controlSize(.small)
                    }
                    .padding(.horizontal, 10)
                    .padding(.vertical, 8)
                    .background(
                        RoundedRectangle(cornerRadius: 8)
                            .fill(Color(NSColor.controlBackgroundColor))
                    )
                }
                .frame(maxWidth: .infinity)

                // Right Column: WeChat QR Code
                VStack(spacing: 10) {
                    HStack(spacing: 6) {
                        Image(systemName: "qrcode")
                            .font(.system(size: 12))
                            .foregroundStyle(.green)
                        Text("微信赞赏收款码")
                            .font(.system(size: 12.5, weight: .semibold))
                    }

                    if let img = qrImage {
                        Image(nsImage: img)
                            .resizable()
                            .aspectRatio(contentMode: .fit)
                            .frame(width: 190, height: 190)
                            .clipShape(RoundedRectangle(cornerRadius: 10))
                            .overlay(
                                RoundedRectangle(cornerRadius: 10)
                                    .strokeBorder(Color.primary.opacity(0.12), lineWidth: 1)
                            )
                            .shadow(color: .black.opacity(0.08), radius: 6, y: 3)
                    } else {
                        Rectangle()
                            .fill(Color.secondary.opacity(0.1))
                            .frame(width: 190, height: 190)
                            .overlay(Text("加载收款码中…").font(.system(size: 12)).foregroundStyle(.secondary))
                            .clipShape(RoundedRectangle(cornerRadius: 10))
                    }

                    Text("微信扫一扫 · 赞赏支持")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
                .padding(12)
                .background(
                    RoundedRectangle(cornerRadius: 10)
                        .fill(Color(NSColor.controlBackgroundColor))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 10)
                        .strokeBorder(Color.primary.opacity(0.06), lineWidth: 1)
                )
                .frame(width: 220)
            }
            .padding(.horizontal, 20)

            Spacer(minLength: 4)

            // Bottom Buttons
            HStack(spacing: 12) {
                Spacer()

                Button {
                    saveQRCodeImage()
                } label: {
                    Label("保存收款码到下载", systemImage: "arrow.down.circle")
                        .font(.system(size: 12))
                }
                .buttonStyle(.bordered)
                .controlSize(.regular)

                Button {
                    onClose()
                } label: {
                    Text("关闭")
                        .font(.system(size: 12))
                        .frame(width: 60)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.regular)
            }
            .padding(.horizontal, 20)
            .padding(.bottom, 16)
        }
        .frame(width: 540, height: 450)
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
            let dest = downloads.appendingPathComponent("ShotX_微信赞赏收款码.jpg")
            try? jpg.write(to: dest)
            NSWorkspace.shared.activateFileViewerSelecting([dest])
            ToastController.shared.show(
                message: "微信收款码已保存至下载文件夹",
                icon: "checkmark.circle.fill",
                tint: .systemGreen,
                duration: 2.0
            )
        }
    }
}
