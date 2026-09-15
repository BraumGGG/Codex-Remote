@echo off
setlocal
"%~dp0CodexBridge.Setup.exe" uninstall --install-dir "%~dp0"
if errorlevel 1 (
  echo 卸载失败，请查看上面的错误信息。
  pause
  exit /b 1
)
echo Codex Bridge 已卸载，设备和项目配置已保留。
pause
