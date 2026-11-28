@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo.
echo  PST 终端安全运营中心
echo  ------------------------------
echo  启动后浏览器访问：http://localhost:8080/
echo  按 Ctrl+C 停止服务
echo.
node --no-warnings server.js
pause
