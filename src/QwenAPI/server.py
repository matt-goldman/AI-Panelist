# coding=utf-8
"""
FastAPI server for Qwen3-TTS.

Provides a simple REST API for text-to-speech synthesis.
See setup-guide.md for installation and usage instructions.
"""

import io
import base64
import logging
from contextlib import asynccontextmanager
from typing import Optional

import torch
import soundfile as sf
from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel

# Configure logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

# Global model reference (loaded at startup)
model = None


class TTSRequest(BaseModel):
    """Request body for TTS synthesis."""
    text: str
    language: str = "Auto"
    speaker: str = "Vivian"
    instruct: Optional[str] = None
    output_format: str = "wav"  # "wav" or "base64"


class TTSResponse(BaseModel):
    """Response for base64 output format."""
    audio: str
    sample_rate: int


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Load model at startup, cleanup at shutdown."""
    global model
    
    logger.info("Loading Qwen3-TTS model...")
    logger.info("This may take a few minutes on first run (downloading model weights)...")
    
    from qwen_tts import Qwen3TTSModel
    
    # Configuration - adjust these based on your hardware
    MODEL_NAME = "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice"
    DEVICE = "cuda:0"  # Use "cpu" if no NVIDIA GPU
    DTYPE = torch.bfloat16  # Use torch.float32 for CPU or older GPUs
    
    # Check CUDA availability
    if DEVICE.startswith("cuda") and not torch.cuda.is_available():
        logger.warning("CUDA not available, falling back to CPU (this will be slower)")
        DEVICE = "cpu"
        DTYPE = torch.float32
    
    try:
        model = Qwen3TTSModel.from_pretrained(
            MODEL_NAME,
            device_map=DEVICE,
            dtype=DTYPE,
            # Uncomment if you have flash-attn installed and using GPU:
            # attn_implementation="flash_attention_2",
        )
        logger.info(f"Model loaded successfully on {DEVICE}")
    except Exception as e:
        logger.error(f"Failed to load model: {e}")
        raise
    
    yield
    
    # Cleanup
    logger.info("Shutting down...")
    model = None


app = FastAPI(
    title="Qwen3-TTS API",
    description="Local TTS API using Qwen3-TTS",
    version="1.0.0",
    lifespan=lifespan,
)


@app.get("/health")
async def health():
    """Health check endpoint."""
    return {"status": "ok", "model_loaded": model is not None}


@app.get("/speakers")
async def list_speakers():
    """List available speakers for CustomVoice model."""
    if model is None:
        raise HTTPException(status_code=503, detail="Model not loaded")
    
    speakers = model.model.get_supported_speakers()
    return {"speakers": speakers}


@app.get("/languages")
async def list_languages():
    """List supported languages."""
    if model is None:
        raise HTTPException(status_code=503, detail="Model not loaded")
    
    languages = model.model.get_supported_languages()
    return {"languages": languages}


@app.post("/tts", response_class=Response)
async def synthesize(request: TTSRequest):
    """
    Synthesize speech from text.
    
    Returns WAV audio bytes by default, or base64-encoded audio if output_format="base64".
    """
    if model is None:
        raise HTTPException(status_code=503, detail="Model not loaded")
    
    if not request.text or not request.text.strip():
        raise HTTPException(status_code=400, detail="Text is required")
    
    try:
        logger.info(f"Synthesizing: '{request.text[:50]}...' with speaker={request.speaker}")
        
        wavs, sr = model.generate_custom_voice(
            text=request.text.strip(),
            language=request.language,
            speaker=request.speaker,
            instruct=request.instruct if request.instruct else None,
        )
        
        # Write to buffer
        buffer = io.BytesIO()
        sf.write(buffer, wavs[0], sr, format="WAV")
        buffer.seek(0)
        audio_bytes = buffer.read()
        
        logger.info(f"Synthesis complete: {len(audio_bytes)} bytes")
        
        if request.output_format == "base64":
            return TTSResponse(
                audio=base64.b64encode(audio_bytes).decode(),
                sample_rate=sr,
            )
        else:
            return Response(
                content=audio_bytes,
                media_type="audio/wav",
                headers={"X-Sample-Rate": str(sr)},
            )
            
    except Exception as e:
        logger.error(f"Synthesis failed: {e}")
        raise HTTPException(status_code=500, detail=str(e))


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8080)
