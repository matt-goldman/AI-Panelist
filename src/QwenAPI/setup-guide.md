# Qwen3-TTS Local API Setup Guide

A step-by-step guide for running Qwen3-TTS as a local REST API on Windows.  
Written for developers who don't work with Python daily.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Install Python](#2-install-python)
3. [Create a Virtual Environment](#3-create-a-virtual-environment)
4. [Install Dependencies](#4-install-dependencies)
5. [Verify GPU Support (Optional but Recommended)](#5-verify-gpu-support-optional-but-recommended)
6. [Download Model Weights (Optional Pre-download)](#6-download-model-weights-optional-pre-download)
7. [Run the Server](#7-run-the-server)
8. [Test the API](#8-test-the-api)
9. [Run as a Background Service](#9-run-as-a-background-service)
10. [Troubleshooting](#10-troubleshooting)

---

## 1. Prerequisites

### Hardware Requirements

| Component | Minimum | Recommended |
|-----------|---------|-------------|
| GPU | NVIDIA with 6GB+ VRAM | NVIDIA with 12GB+ VRAM |
| RAM | 16GB | 32GB |
| Disk | 10GB free | 20GB free |

> **Note:** CPU-only mode works but is significantly slower (~10x). GPU (CUDA) is strongly recommended.

### Software Requirements

- **Windows 10** (version 1903+) or **Windows 11**
- **NVIDIA GPU Driver** - version 510+ for CUDA support
  - Check: Open PowerShell and run `nvidia-smi`
  - Download: https://www.nvidia.com/download/index.aspx

---

## 2. Install Python

### Option A: Microsoft Store (Easiest)

1. Open Microsoft Store
2. Search for "Python 3.12"
3. Click "Get" / "Install"
4. Verify installation:
   ```powershell
   python --version
   # Should show: Python 3.12.x
   ```

### Option B: Python.org Installer

1. Download from https://www.python.org/downloads/
2. Run the installer
3. **IMPORTANT:** Check "Add Python to PATH" at the bottom of the installer
4. Click "Install Now"
5. Verify:
   ```powershell
   python --version
   pip --version
   ```

### Option C: winget (if you have it)

```powershell
winget install Python.Python.3.12
```

Close and reopen PowerShell after installation.

---

## 3. Create a Virtual Environment

A virtual environment isolates Python packages for this project from other Python projects on your system.

```powershell
# Navigate to this repository
cd E:\repos\Qwen3-TTS

# Create a virtual environment named 'venv'
python -m venv venv

# Activate it (you'll need to do this each time you open a new terminal)
.\venv\Scripts\Activate

# You should see (venv) at the start of your prompt:
# (venv) PS E:\repos\Qwen3-TTS>
```

> **Tip:** If you get an execution policy error, run:
> ```powershell
> Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
> ```

---

## 4. Install Dependencies

With your virtual environment activated:

```powershell
# Upgrade pip first
python -m pip install --upgrade pip

# Install the qwen-tts package (includes PyTorch, transformers, etc.)
pip install qwen-tts

# Install FastAPI server dependencies
pip install -r local_api/requirements.txt
```

This will download approximately 2-3GB of packages.

### Optional: Install Flash Attention 2 (GPU only, faster inference)

Flash Attention significantly speeds up inference on NVIDIA GPUs. It requires:
- NVIDIA GPU with compute capability 7.0+ (RTX 20-series or newer)
- CUDA toolkit

```powershell
# This can take 10-20 minutes to compile
pip install flash-attn --no-build-isolation
```

If this fails, skip it - the server will work without it, just slightly slower.

---

## 5. Verify GPU Support (Optional but Recommended)

Check that PyTorch can see your GPU:

```powershell
python -c "import torch; print(f'CUDA available: {torch.cuda.is_available()}'); print(f'GPU: {torch.cuda.get_device_name(0) if torch.cuda.is_available() else \"None\"}')"
```

Expected output (example):
```
CUDA available: True
GPU: NVIDIA GeForce RTX 3080
```

If CUDA is not available:
- Make sure NVIDIA drivers are installed (`nvidia-smi` should work)
- The server will fall back to CPU mode (slower but functional)

---

## 6. Download Model Weights (Optional Pre-download)

Model weights (~3.5GB) are automatically downloaded on first run. If you prefer to pre-download:

```powershell
# Using Hugging Face CLI
pip install -U "huggingface_hub[cli]"
huggingface-cli download Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice --local-dir ./models/Qwen3-TTS-12Hz-1.7B-CustomVoice
huggingface-cli download Qwen/Qwen3-TTS-Tokenizer-12Hz --local-dir ./models/Qwen3-TTS-Tokenizer-12Hz
```

If pre-downloaded, update `MODEL_NAME` in `local_api/server.py` to the local path.

---

## 7. Run the Server

### Basic Run (Foreground)

```powershell
# Make sure venv is activated
.\venv\Scripts\Activate

# Run the server
python local_api/server.py
```

First run will:
1. Download model weights (~3.5GB) - takes 5-15 minutes depending on connection
2. Load model into GPU memory - takes 30-60 seconds

You'll see:
```
INFO:     Loading Qwen3-TTS model...
INFO:     This may take a few minutes on first run (downloading model weights)...
INFO:     Model loaded successfully on cuda:0
INFO:     Uvicorn running on http://0.0.0.0:8080 (Press CTRL+C to quit)
```

### Run with Custom Port

```powershell
uvicorn local_api.server:app --host 0.0.0.0 --port 8080
```

---

## 8. Test the API

### Using curl (PowerShell)

```powershell
# Health check
Invoke-RestMethod -Uri "http://localhost:8080/health"

# List speakers
Invoke-RestMethod -Uri "http://localhost:8080/speakers"

# Synthesize speech and save to file
$body = @{
    text = "Hello! This is a test of the Qwen3 text to speech system."
    speaker = "Ryan"
    language = "English"
} | ConvertTo-Json

Invoke-WebRequest -Uri "http://localhost:8080/tts" `
    -Method POST `
    -ContentType "application/json" `
    -Body $body `
    -OutFile "test_output.wav"

# Play the audio (Windows)
Start-Process "test_output.wav"
```

### Using the Interactive API Docs

Open your browser to: http://localhost:8080/docs

This shows an interactive Swagger UI where you can test all endpoints.

### Using .NET

```csharp
using var client = new HttpClient();
client.BaseAddress = new Uri("http://localhost:8080");

var response = await client.PostAsJsonAsync("/tts", new {
    text = "Hello from .NET!",
    speaker = "Ryan",
    language = "English"
});

var audioBytes = await response.Content.ReadAsByteArrayAsync();
await File.WriteAllBytesAsync("output.wav", audioBytes);
```

---

## 9. Run as a Background Service

### Option A: PowerShell Background Job

```powershell
# Start as background job
Start-Job -Name "QwenTTS" -ScriptBlock {
    Set-Location "E:\repos\Qwen3-TTS"
    & ".\venv\Scripts\python.exe" "local_api/server.py"
}

# Check status
Get-Job -Name "QwenTTS"

# View output/errors
Receive-Job -Name "QwenTTS"

# Stop when done
Stop-Job -Name "QwenTTS"
Remove-Job -Name "QwenTTS"
```

### Option B: Separate PowerShell Window

```powershell
# Start in a new minimized window
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd E:\repos\Qwen3-TTS; .\venv\Scripts\Activate; python local_api/server.py" -WindowStyle Minimized
```

### Option C: Windows Task Scheduler (Start on Login)

1. Open Task Scheduler (`taskschd.msc`)
2. Click "Create Basic Task..."
3. Name: "Qwen3-TTS Server"
4. Trigger: "When I log on"
5. Action: "Start a program"
6. Program: `E:\repos\Qwen3-TTS\venv\Scripts\python.exe`
7. Arguments: `E:\repos\Qwen3-TTS\local_api\server.py`
8. Start in: `E:\repos\Qwen3-TTS`

### Option D: NSSM (Run as Windows Service)

For a proper Windows service:

1. Download NSSM: https://nssm.cc/download
2. Extract and run:
   ```powershell
   nssm install QwenTTS "E:\repos\Qwen3-TTS\venv\Scripts\python.exe" "E:\repos\Qwen3-TTS\local_api\server.py"
   nssm set QwenTTS AppDirectory "E:\repos\Qwen3-TTS"
   nssm start QwenTTS
   ```

---

## 10. Troubleshooting

### "python is not recognized"

Python is not in your PATH. Either:
- Reinstall Python and check "Add to PATH"
- Or use the full path: `C:\Users\<you>\AppData\Local\Programs\Python\Python312\python.exe`

### "CUDA out of memory"

Your GPU doesn't have enough VRAM. Options:
1. Use the smaller 0.6B model - edit `server.py`:
   ```python
   MODEL_NAME = "Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice"
   ```
2. Close other GPU-intensive applications
3. Fall back to CPU mode (slower)

### "Model download is slow/failing"

- Check your internet connection
- Try pre-downloading (see Section 6)
- For users in China, use ModelScope instead of Hugging Face

### "Flash attention installation fails"

Skip it - it's optional. Remove or comment out `attn_implementation="flash_attention_2"` in `server.py`.

### "Port 8080 is already in use"

Change the port:
```powershell
uvicorn local_api.server:app --port 8081
```

### Server crashes after a while

This is usually GPU memory fragmentation. Restart the server periodically, or add to `server.py`:
```python
import gc
torch.cuda.empty_cache()
gc.collect()
```

### Check server logs

If running as a background job:
```powershell
Receive-Job -Name "QwenTTS" -Keep
```

---

## Quick Reference

| Command | Purpose |
|---------|---------|
| `.\venv\Scripts\Activate` | Activate virtual environment |
| `python local_api/server.py` | Start the server |
| `deactivate` | Exit virtual environment |
| `Invoke-RestMethod http://localhost:8080/health` | Health check |

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/health` | GET | Health check |
| `/speakers` | GET | List available voices |
| `/languages` | GET | List supported languages |
| `/tts` | POST | Synthesize speech |
| `/docs` | GET | Interactive API documentation |

## Available Speakers (CustomVoice model)

| Speaker | Description | Best For |
|---------|-------------|----------|
| Vivian | Bright young female | Chinese |
| Serena | Warm gentle female | Chinese |
| Uncle_Fu | Low mellow male | Chinese |
| Dylan | Clear Beijing male | Chinese |
| Eric | Lively Sichuan male | Chinese |
| **Ryan** | Dynamic male | **English** |
| **Aiden** | Sunny American male | **English** |
| Ono_Anna | Playful female | Japanese |
| Sohee | Warm female | Korean |

---

## Next Steps

- See [local-options.md](local-options.md) for voice customization and streaming options
- Check the main [README.md](README.md) for advanced usage
