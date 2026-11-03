import Cocoa

/// 安全的事件监听器包装，基于 RAII 思想，在对象释放或重新赋值时自动移除 NSEvent monitor，彻底消除事件泄露与悬挂闭包
final class EventMonitorToken {
    private var monitor: Any?

    init(global mask: NSEvent.EventTypeMask, handler: @escaping (NSEvent) -> Void) {
        self.monitor = NSEvent.addGlobalMonitorForEvents(matching: mask, handler: handler)
    }

    init(local mask: NSEvent.EventTypeMask, handler: @escaping (NSEvent) -> NSEvent?) {
        self.monitor = NSEvent.addLocalMonitorForEvents(matching: mask, handler: handler)
    }

    func cancel() {
        if let m = monitor {
            NSEvent.removeMonitor(m)
            monitor = nil
        }
    }

    deinit {
        cancel()
    }
}

