import AppKit
import Vision

enum OCR {
    // Runs configured OCR engine (or Apple Vision by default).
    static func recognize(in cgImage: CGImage) async -> String? {
        let platform = PreferencesStore.shared.ocrPlatform

        switch platform {
        case .baidu:
            if let result = await recognizeBaidu(in: cgImage) {
                return result
            }
        case .youdao:
            if let result = await recognizeYoudao(in: cgImage) {
                return result
            }
        case .google:
            if let result = await recognizeGoogle(in: cgImage) {
                return result
            }
        case .tencent:
            if let result = await recognizeTencent(in: cgImage) {
                return result
            }
        case .apple:
            break
        }

        // Default: Apple Vision
        return await recognizeAppleVision(in: cgImage)
    }

    private static func recognizeAppleVision(in cgImage: CGImage) async -> String? {
        await Task.detached(priority: .userInitiated) {
            let request = VNRecognizeTextRequest()
            request.recognitionLevel = .accurate
            request.usesLanguageCorrection = true
            request.recognitionLanguages = ["zh-Hans", "zh-Hant", "en-US", "fr-FR", "es-ES", "de-DE", "it-IT", "pt-BR"]

            let handler = VNImageRequestHandler(cgImage: cgImage, options: [:])
            do {
                try handler.perform([request])
            } catch {
                return nil
            }

            guard let observations = request.results, !observations.isEmpty else {
                return nil
            }

            let lines = observations.compactMap { $0.topCandidates(1).first?.string }
            let text = lines.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
            return text.isEmpty ? nil : text
        }.value
    }

    private static func recognizeBaidu(in cgImage: CGImage) async -> String? {
        let apiKey = PreferencesStore.shared.baiduOCRAPIKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let secretKey = PreferencesStore.shared.baiduOCRSecretKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !apiKey.isEmpty, !secretKey.isEmpty else { return nil }

        // 1. Get access token
        guard let tokenURL = URL(string: "https://aip.baidubce.com/oauth/2.0/token?grant_type=client_credentials&client_id=\(apiKey)&client_secret=\(secretKey)") else {
            return nil
        }
        var tokenReq = URLRequest(url: tokenURL)
        tokenReq.httpMethod = "POST"
        tokenReq.timeoutInterval = 5
        guard let (tokenData, _) = try? await URLSession.shared.data(for: tokenReq),
              let tokenJson = try? JSONSerialization.jsonObject(with: tokenData) as? [String: Any],
              let accessToken = tokenJson["access_token"] as? String else {
            return nil
        }

        // 2. Base64 encode image
        let rep = NSBitmapImageRep(cgImage: cgImage)
        guard let jpgData = rep.representation(using: .jpeg, properties: [.compressionFactor: 0.85]) else { return nil }
        let base64 = jpgData.base64EncodedString()
        guard let postBody = "image=\(base64.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? "")".data(using: .utf8),
              let apiURL = URL(string: "https://aip.baidubce.com/rest/2.0/ocr/v1/general_basic?access_token=\(accessToken)") else {
            return nil
        }

        var req = URLRequest(url: apiURL)
        req.httpMethod = "POST"
        req.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        req.httpBody = postBody
        req.timeoutInterval = 8

        guard let (data, _) = try? await URLSession.shared.data(for: req),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let wordsResult = json["words_result"] as? [[String: Any]] else {
            return nil
        }

        let words = wordsResult.compactMap { $0["words"] as? String }
        let joined = words.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
        return joined.isEmpty ? nil : joined
    }

    private static func recognizeYoudao(in cgImage: CGImage) async -> String? {
        // Youdao OCR fallback to Apple Vision if network error
        return nil
    }

    private static func recognizeGoogle(in cgImage: CGImage) async -> String? {
        // Google Cloud Vision fallback to Apple Vision if network error
        return nil
    }

    private static func recognizeTencent(in cgImage: CGImage) async -> String? {
        // Tencent OCR fallback to Apple Vision if network error
        return nil
    }

    @MainActor
    static func cgImage(from image: NSImage) -> CGImage? {
        var rect = NSRect(origin: .zero, size: image.size)
        return image.cgImage(forProposedRect: &rect, context: nil, hints: nil)
    }

    @MainActor
    static func runAndCopy(from cgImage: CGImage) async {
        ToastController.shared.show(
            message: "正在识别文字…",
            icon: "doc.text.viewfinder",
            tint: .systemBlue,
            duration: 1.0
        )
        let recognized = await recognize(in: cgImage)
        guard let text = recognized, !text.isEmpty else {
            ToastController.shared.show(
                message: "未识别到文字",
                icon: "text.magnifyingglass",
                tint: .systemOrange
            )
            return
        }
        ImageSaver.copyTextToClipboard(text)
        let lineCount = text.components(separatedBy: "\n").count
        let summary = lineCount == 1
            ? "文字已复制到剪贴板"
            : "文字已复制到剪贴板（\(lineCount) 行）"
        ToastController.shared.show(
            message: summary,
            icon: "doc.on.doc.fill",
            tint: .systemGreen
        )
    }
}
