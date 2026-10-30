import Cocoa
import Sparkle

@MainActor
final class UpdaterController {
    static let shared = UpdaterController()

    let controller: SPUStandardUpdaterController
    private var isChecking = false

    private init() {
        controller = SPUStandardUpdaterController(
            startingUpdater: true,
            updaterDelegate: nil,
            userDriverDelegate: nil
        )
    }

    func checkForUpdates() {
        guard !isChecking else { return }
        isChecking = true

        ToastController.shared.show(
            message: "正在检查更新…",
            icon: "arrow.triangle.2.circlepath",
            tint: .systemBlue,
            duration: 1.5
        )

        Task {
            await checkGitHubRelease()
            isChecking = false
        }
    }

    private func checkGitHubRelease() async {
        guard let url = URL(string: "https://api.github.com/repos/muzi86754781/shotx/releases/latest") else {
            fallbackToSparkle()
            return
        }

        var request = URLRequest(url: url)
        request.setValue("application/vnd.github.v3+json", forHTTPHeaderField: "Accept")
        request.timeoutInterval = 8

        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode),
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let tagName = json["tag_name"] as? String else {
                fallbackToSparkle()
                return
            }

            let releaseNotes = json["body"] as? String ?? ""
            let htmlURL = (json["html_url"] as? String).flatMap { URL(string: $0) }
            let currentVer = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0"
            let remoteVer = tagName.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))

            if isVersion(remoteVer, greaterThan: currentVer) {
                showUpdateAlert(tagName: tagName, notes: releaseNotes, openURL: htmlURL)
            } else {
                ToastController.shared.show(
                    message: "当前已是最新版本 (v\(currentVer))",
                    icon: "checkmark.circle.fill",
                    tint: .systemGreen,
                    duration: 2.0
                )
            }
        } catch {
            fallbackToSparkle()
        }
    }

    private func fallbackToSparkle() {
        controller.checkForUpdates(nil)
    }

    private func isVersion(_ v1: String, greaterThan v2: String) -> Bool {
        v1.compare(v2, options: .numeric) == .orderedDescending
    }

    private func showUpdateAlert(tagName: String, notes: String, openURL: URL?) {
        let alert = NSAlert()
        alert.messageText = "发现新版本 \(tagName)"
        alert.informativeText = notes.isEmpty ? "有新版本可用，是否立即前往下载？" : notes
        alert.addButton(withTitle: "前往下载")
        alert.addButton(withTitle: "稍后")
        alert.alertStyle = .informational

        NSApp.activate(ignoringOtherApps: true)
        let response = alert.runModal()
        if response == .alertFirstButtonReturn {
            if let target = openURL ?? URL(string: "https://github.com/muzi86754781/shotx/releases/latest") {
                NSWorkspace.shared.open(target)
            }
        }
    }
}
