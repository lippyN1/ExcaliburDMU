import sys
import os
import subprocess
import json
import ctypes

from PySide6.QtWidgets import QApplication, QMainWindow, QMessageBox
from PySide6.QtWebEngineWidgets import QWebEngineView
from PySide6.QtWebEngineCore import QWebEngineSettings, QWebEngineScript
from PySide6.QtWebChannel import QWebChannel
from PySide6.QtCore import Qt, QUrl, QObject, Slot, QFile, QIODevice

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
EXE_PATH = os.path.abspath(os.path.join(BASE_DIR, "..", "dist", "Excalibur.exe"))


# ---------- UAC ----------
def is_admin() -> bool:
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def relaunch_as_admin() -> bool:
    """Перезапуск текущего скрипта с правами администратора через UAC."""
    params = " ".join(f'"{a}"' for a in sys.argv)
    rc = ctypes.windll.shell32.ShellExecuteW(
        None, "runas", sys.executable, params, None, 1
    )
    return rc > 32  # >32 — успех; 5 — пользователь отклонил UAC


# ---------- Мост Python <-> JS ----------
class Bridge(QObject):
    @Slot(result=str)
    def get_disks(self):
        try:
            if not os.path.exists(EXE_PATH):
                return json.dumps(
                    {"error": f"Excalibur.exe не найден: {EXE_PATH}"},
                    ensure_ascii=False,
                )

            result = subprocess.run(
                [EXE_PATH, "--dump-devices", "--compact"],
                capture_output=True,
                text=True,
                encoding="utf-8",
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            if result.returncode != 0:
                return json.dumps(
                    {"error": result.stderr or f"returncode={result.returncode}"},
                    ensure_ascii=False,
                )

            # Проверяем, что это валидный JSON — иначе отдаём ошибку как есть
            try:
                data = json.loads(result.stdout)
            except json.JSONDecodeError as e:
                return json.dumps(
                    {"error": f"Невалидный JSON от Excalibur.exe: {e}"},
                    ensure_ascii=False,
                )

            return json.dumps(data, ensure_ascii=False)
        except Exception as e:
            return json.dumps({"error": str(e)}, ensure_ascii=False)


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Excalibur")
        self.resize(1600, 900)
        self.setWindowFlags(Qt.FramelessWindowHint)

        self.browser = QWebEngineView()

        self.browser.settings().setAttribute(
            QWebEngineSettings.WebAttribute.LocalContentCanAccessFileUrls, True
        )
        self.browser.settings().setAttribute(
            QWebEngineSettings.WebAttribute.LocalContentCanAccessRemoteUrls, True
        )
        self.browser.settings().setAttribute(
            QWebEngineSettings.WebAttribute.JavascriptEnabled, True
        )

        # Мост
        self.channel = QWebChannel()
        self.bridge = Bridge()
        self.channel.registerObject("bridge", self.bridge)
        self.browser.page().setWebChannel(self.channel)

        # Инъекция qwebchannel.js (обход qrc:// в Qt 6.7+)
        qwc = QFile(":/qtwebchannel/qwebchannel.js")
        if qwc.open(QIODevice.OpenModeFlag.ReadOnly):
            js = bytes(qwc.readAll()).decode("utf-8", errors="replace")
            qwc.close()

            script = QWebEngineScript()
            script.setName("__qwebchannel_inject__")
            script.setSourceCode(js)
            script.setInjectionPoint(
                QWebEngineScript.InjectionPoint.DocumentCreation
            )
            script.setWorldId(QWebEngineScript.ScriptWorldId.MainWorld)
            script.setRunsOnSubFrames(False)
            self.browser.page().scripts().insert(script)
            print("[INFO] qwebchannel.js инъектирован")
        else:
            print("[WARN] Не удалось загрузить qwebchannel.js из ресурсов Qt")

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

    if not is_admin():
        QMessageBox.warning(
            window,
            "Требуются права администратора",
            "Excalibur запущен без прав администратора.\n"
            "Список дисков и SMART-данные могут быть недоступны.\n\n"
            "Перезапустите приложение и подтвердите запрос UAC.",
        )

    sys.exit(app.exec())