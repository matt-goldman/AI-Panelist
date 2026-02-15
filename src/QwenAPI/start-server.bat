@echo off
echo Starting Qwen3-TTS Server...
echo.

cd /d "%~dp0.."

if not exist "venv\Scripts\python.exe" (
    echo ERROR: Virtual environment not found.
    echo Please run setup first. See setup-guide.md
    pause
    exit /b 1
)

echo Activating virtual environment...
call venv\Scripts\activate.bat

echo Starting server on http://localhost:8080
echo Press Ctrl+C to stop.
echo.

python local_api\server.py

pause
