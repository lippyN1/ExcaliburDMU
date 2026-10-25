import sys
import os
import subprocess
import json
import ctypes
import time
from PySide6.QtWidgets import QApplication, QMainWindow
from PySide6.QtWebEngineWidgets import QWebEngineView
from PySide6.QtWebEngineCore import QWebEngineSettings, QWebEngineScript
from PySide6.QtWebChannel import QWebChannel
from PySide6.QtCore import Qt, QUrl, QObject, Slot, QFile, QIODevice, QThread, Signal

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
EXE_PATH = os.path.abspath(os.path.join(BASE_DIR, "..", "dist", "Excalibur.exe"))

def is_admin() -> bool:
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False

def relaunch_as_admin() -> bool:
    params = " ".join(f'"{a}"' for a in sys.argv)
    rc = ctypes.windll.shell32.ShellExecuteW(None, "runas", sys.executable, params, None, 1)
    return rc > 32

class ProcessThread(QThread):
    progress = Signal(float, float)
    phase = Signal(str)
    result = Signal(str, str)

    def __init__(self, args):
        super().__init__()
        self.args = args

    def run(self):
        try:
            process = subprocess.Popen(
                self.args,
                stdout=subprocess.PIPE, text=True, bufsize=1, encoding="utf-8",
                creationflags=subprocess.CREATE_NO_WINDOW
            )
            for line in process.stdout:
                line = line.strip()
                if line.startswith('{"progress"'):
                    data = json.loads(line)
                    self.progress.emit(data.get('progress', 0), data.get('speed', 0))
                elif line.startswith('PHASE: format'):
                    self.phase.emit("format")
                    for i in range(1, 11):
                        time.sleep(0.5)
                        self.progress.emit(i * 10, 0)
                elif line.startswith('RESULT:'):
                    data = json.loads(line[7:].strip())
                    self.result.emit("Capacity Test", json.dumps(data))
            process.wait()
        except Exception as e:
            self.result.emit("Error", json.dumps({"verdict": "Unknown"}))

class Bridge(QObject):
    def __init__(self):
        super().__init__()
        self.window = None
        self.thread = None

    @Slot(result=str)
    def get_disks(self):
        try:
            if not os.path.exists(EXE_PATH):
                return json.dumps({"error": "EXE не найден"})

            result = subprocess.run(
                [EXE_PATH, "--dump-devices", "--compact"],
                capture_output=True, text=True, encoding="utf-8",
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            if result.returncode != 0:
                return json.dumps({"error": result.stderr or "Ошибка EXE"})

            data = json.loads(result.stdout)

            for disk in data.get("Disks", []):
                idx = disk.get("DiskNumber")
                ps = f"(Get-Disk -Number {idx} -ErrorAction SilentlyContinue).PartitionStyle"
                try:
                    ps_res = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
                    style = ps_res.stdout.strip().upper()
                    disk["PartitionStyle"] = style if style else "RAW"
                except Exception:
                    disk["PartitionStyle"] = "RAW"

            return json.dumps(data, ensure_ascii=False)
        except Exception as e:
            return json.dumps({"error": str(e)}, ensure_ascii=False)

    @Slot(int)
    def start_surface_test(self, disk_index):
        self._run_thread([EXE_PATH, "--test-surface", str(disk_index)])

    @Slot(int)
    def start_capacity_test(self, disk_index):
        self._run_thread([EXE_PATH, "--test-capacity", str(disk_index)])

    def _run_thread(self, args):
        if self.thread and self.thread.isRunning():
            return
        self.thread = ProcessThread(args)
        self.thread.progress.connect(self._on_progress)
        self.thread.phase.connect(self._on_phase)
        self.thread.result.connect(self._on_result)
        self.thread.start()

    def _on_progress(self, percent, speed):
        if self.window:
            self.window.browser.page().runJavaScript(f"updateProgress({percent}, {speed})")

    def _on_phase(self, phase):
        if self.window and phase == "format":
            self.window.browser.page().runJavaScript("updateTestPhase('format')")

    def _on_result(self, title, json_str):
        if self.window:
            safe_json = json_str.replace("\\", "\\\\").replace("'", "\\'")
            self.window.browser.page().runJavaScript(f"showResult('{title}', '{safe_json}')")

    @Slot()
    def cancel_test(self):
        if self.thread and self.thread.isRunning():
            self.thread.terminate()
            self.thread.wait(1000)

    @Slot()
    def minimize(self):
        if self.window:
            self.window.showMinimized()

    @Slot()
    def maximize(self):
        if self.window:
            if self.window.isMaximized():
                self.window.showNormal()
            else:
                self.window.showMaximized()

    @Slot()
    def close(self):
        if self.window:
            self.window.close()

class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Excalibur")
        self.resize(1600, 900)
        self.setWindowFlags(Qt.FramelessWindowHint)

        self.browser = QWebEngineView()
        self.browser.settings().setAttribute(QWebEngineSettings.WebAttribute.LocalContentCanAccessFileUrls, True)
        self.browser.settings().setAttribute(QWebEngineSettings.WebAttribute.JavascriptEnabled, True)

        self.channel = QWebChannel()
        self.bridge = Bridge()
        self.bridge.window = self
        self.channel.registerObject("bridge", self.bridge)
        self.browser.page().setWebChannel(self.channel)

        qwc = QFile(":/qtwebchannel/qwebchannel.js")
        if qwc.open(QIODevice.OpenModeFlag.ReadOnly):
            js = bytes(qwc.readAll()).decode("utf-8", errors="replace")
            qwc.close()
            script = QWebEngineScript()
            script.setName("__qwebchannel_inject__")
            script.setSourceCode(js)
            script.setInjectionPoint(QWebEngineScript.InjectionPoint.DocumentCreation)
            script.setWorldId(QWebEngineScript.ScriptWorldId.MainWorld)
            script.setRunsOnSubFrames(False)
            self.browser.page().scripts().insert(script)

        html_path = os.path.join(BASE_DIR, "index.html")
        self.browser.load(QUrl.fromLocalFile(html_path))
        self.setCentralWidget(self.browser)

if __name__ == "__main__":
    if not is_admin():
        if relaunch_as_admin():
            sys.exit(0)
    app = QApplication(sys.argv)
    window = MainWindow()
    window.show()
    sys.exit(app.exec())
