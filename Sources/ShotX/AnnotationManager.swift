import Cocoa

/// 标注历史与图元状态管理器：解耦标注生命周期、图元集合与撤销重做栈
final class AnnotationManager {
    var annotations: [Annotation] = []
    var undoneAnnotations: [Annotation] = []
    var nextStepNumber: Int = 1

    var canUndo: Bool { !annotations.isEmpty }
    var canRedo: Bool { !undoneAnnotations.isEmpty }

    func addAnnotation(_ annotation: Annotation) {
        annotations.append(annotation)
        undoneAnnotations.removeAll()
        if case .step = annotation.shape {
            nextStepNumber += 1
        }
    }

    @discardableResult
    func undo() -> Annotation? {
        guard let removed = annotations.popLast() else { return nil }
        undoneAnnotations.append(removed)
        if case .step = removed.shape {
            nextStepNumber = max(1, nextStepNumber - 1)
        }
        return removed
    }

    @discardableResult
    func redo() -> Annotation? {
        guard let restored = undoneAnnotations.popLast() else { return nil }
        annotations.append(restored)
        if case .step = restored.shape {
            nextStepNumber += 1
        }
        return restored
    }

    func clear() {
        annotations.removeAll()
        undoneAnnotations.removeAll()
        nextStepNumber = 1
    }

    func clearUndone() {
        undoneAnnotations.removeAll()
    }
}
