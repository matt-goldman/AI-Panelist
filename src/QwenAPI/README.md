# Local API

This folder contains a FastAPI wrapper for running Qwen3-TTS as a local REST API.

## Files

- **server.py** - FastAPI server implementation
- **requirements.txt** - Additional dependencies (FastAPI, uvicorn)
- **start-server.bat** - Double-click to start the server (Windows)

## Quick Start

See [setup-guide.md](../setup-guide.md) for detailed instructions.

```powershell
# From repo root, with venv activated:
python local_api/server.py
```

Or double-click `start-server.bat` (after completing setup).

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/health` | GET | Health check |
| `/speakers` | GET | List available voices |
| `/languages` | GET | List supported languages |
| `/tts` | POST | Synthesize speech |
| `/docs` | GET | Interactive Swagger UI |

## Example Request

```json
POST /tts
{
    "text": "Hello world!",
    "speaker": "Ryan",
    "language": "English",
    "instruct": null,
    "output_format": "wav"
}
```

Response: WAV audio bytes (or base64 JSON if `output_format: "base64"`)
