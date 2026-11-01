import Cocoa
import SwiftUI

final class AboutWindowController {
    static let shared = AboutWindowController()

    private var window: NSWindow?

    func show() {
        if let w = window {
            w.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }

        let view = AboutView { [weak self] in
            self?.window?.close()
            self?.window = nil
        }

        let hosting = NSHostingController(rootView: view)
        let w = NSWindow(contentViewController: hosting)
        w.title = "关于 ShotX"
        w.styleMask = [.titled, .closable]
        w.setContentSize(NSSize(width: 520, height: 430))
        w.isReleasedWhenClosed = false
        w.center()
        w.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        self.window = w
    }
}

struct AboutView: View {
    let onClose: () -> Void

    var versionString: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.9.9"
    }

    var body: some View {
        VStack(spacing: 16) {
            // Header with App Icon
            HStack(spacing: 16) {
                if let icon = NSApp.applicationIconImage {
                    Image(nsImage: icon)
                        .resizable()
                        .frame(width: 64, height: 64)
                        .shadow(color: .black.opacity(0.15), radius: 6, y: 3)
                } else {
                    ZStack {
                        RoundedRectangle(cornerRadius: 14)
                            .fill(Color.accentColor)
                            .frame(width: 64, height: 64)
                        Image(systemName: "camera.viewfinder")
                            .font(.system(size: 32, weight: .bold))
                            .foregroundStyle(.white)
                    }
                }

                VStack(alignment: .leading, spacing: 4) {
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Text("ShotX")
                            .font(.system(size: 22, weight: .bold))
                        Text("v\(versionString)")
                            .font(.system(size: 13, weight: .semibold, design: .monospaced))
                            .foregroundStyle(.secondary)
                            .padding(.horizontal, 6)
                            .padding(.vertical, 2)
                            .background(Capsule().fill(Color.primary.opacity(0.06)))
                    }

                    Text("现代 macOS 全能屏幕截图、就地标注、长截图与录屏录音工具")
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)

                    HStack(spacing: 8) {
                        Text("开发者:")
                            .font(.system(size: 11.5))
                            .foregroundStyle(.secondary)
                        Link("@muzi86754781", destination: URL(string: "https://github.com/muzi86754781")!)
                            .font(.system(size: 11.5))
                        Text("·")
                            .foregroundStyle(.tertiary)
                        Text("QQ: 285351298")
                            .font(.system(size: 11.5, design: .monospaced))
                            .foregroundStyle(.secondary)
                    }
                }

                Spacer()
            }
            .padding(.horizontal, 20)
            .padding(.top, 18)

            Divider()

            // Feature Highlights Grid (Fixed layout, NO scrollbars!)
            VStack(alignment: .leading, spacing: 8) {
                Text("功能特性")
                    .font(.system(size: 12.5, weight: .semibold))
                    .foregroundStyle(.secondary)

                LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 8) {
                    featureItem(icon: "camera.viewfinder", title: "全能辅助截图", desc: "取色放大镜、九宫格比例与常用尺寸")
                    featureItem(icon: "arrow.up.and.down", title: "滚动长截图", desc: "内容滚动检测智能无缝拼接")
                    featureItem(icon: "doc.text.viewfinder", title: "截图 OCR 识别", desc: "离线高精度识别，一键复制文本")
                    featureItem(icon: "video.fill", title: "屏幕高清录制", desc: "麦克风/摄像头/按键显示，MP4/GIF")
                    featureItem(icon: "mic.fill", title: "麦克风声音录制", desc: "独立高音质录音，悬浮药丸计时")
                    featureItem(icon: "character.book.closed.fill", title: "多模态翻译", desc: "划词翻译、截图翻译与双语朗读")
                    featureItem(icon: "doc.on.clipboard.fill", title: "剪贴板历史", desc: "记录复制内容，双击直接粘贴")
                    featureItem(icon: "eye.slash.fill", title: "桌面图标管理", desc: "一键隐藏与恢复桌面，录屏更整洁")
                }
            }
            .padding(.horizontal, 20)

            Spacer(minLength: 4)

            Divider()

            // Footer Actions
            HStack(spacing: 10) {
                Button {
                    SupportAuthorController.shared.show()
                } label: {
                    Label("支持作者", systemImage: "cup.and.saucer.fill")
                        .font(.system(size: 12))
                }
                .buttonStyle(.borderedProminent)
                .tint(.orange)
                .controlSize(.regular)

                Button("检查更新") {
                    UpdaterController.shared.checkForUpdates()
                }
                .buttonStyle(.bordered)
                .controlSize(.regular)

                Button {
                    if let url = URL(string: "https://github.com/muzi86754781/shotx") {
                        NSWorkspace.shared.open(url)
                    }
                } label: {
                    Label("GitHub", systemImage: "arrow.up.right.square")
                        .font(.system(size: 12))
                }
                .buttonStyle(.bordered)
                .controlSize(.regular)

                Spacer()

                Button("关闭") {
                    onClose()
                }
                .buttonStyle(.bordered)
                .controlSize(.regular)
                .keyboardShortcut(.cancelAction)
            }
            .padding(.horizontal, 20)
            .padding(.bottom, 16)
        }
        .frame(width: 520, height: 430)
        .background(Color(NSColor.windowBackgroundColor))
    }

    private func featureItem(icon: String, title: String, desc: String) -> some View {
        HStack(spacing: 8) {
            Image(systemName: icon)
                .font(.system(size: 13))
                .foregroundStyle(Color.accentColor)
                .frame(width: 22, height: 22)
                .background(Color.accentColor.opacity(0.1))
                .clipShape(RoundedRectangle(cornerRadius: 6))

            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                    .font(.system(size: 11.5, weight: .medium))
                Text(desc)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer()
        }
        .padding(6)
        .background(Color(NSColor.controlBackgroundColor))
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }
}
