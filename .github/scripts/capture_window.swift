// Captures one visible window owned by the screenshot process on native macOS.
import Foundation
import CoreGraphics

guard (3...4).contains(CommandLine.arguments.count),
      let pid = Int32(CommandLine.arguments[1]) else {
    fatalError("Usage: capture_window PID OUTPUT.png [TITLE_FRAGMENT]")
}
let destination = CommandLine.arguments[2]
let titleFragment = CommandLine.arguments.count == 4 ? CommandLine.arguments[3] : " — drcom4scut "
guard !titleFragment.isEmpty else { fatalError("A nonempty window title is required") }
let deadline = Date().addingTimeInterval(30)
while Date() < deadline {
    if let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
        as? [[String: Any]],
       let window = windows.first(where: {
           (pid == 0 || ($0[kCGWindowOwnerPID as String] as? Int32) == pid)
           && ($0[kCGWindowLayer as String] as? Int) == 0
           && ($0[kCGWindowName as String] as? String)?.contains(titleFragment) == true
       }),
       let identifier = window[kCGWindowNumber as String] as? Int {
        Thread.sleep(forTimeInterval: 1)
        let capture = Process()
        capture.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        capture.arguments = ["-x", "-o", "-l", String(identifier), destination]
        try capture.run()
        capture.waitUntilExit()
        guard capture.terminationStatus == 0 else {
            fatalError("macOS window capture failed; a native desktop with screen-capture permission is required.")
        }
        print("Captured native macOS window \(identifier) owned by PID \(pid)")
        exit(0)
    }
    Thread.sleep(forTimeInterval: 0.25)
}
fatalError("No ready, visible drcom4scutGUI window owned by the requested process was found.")
