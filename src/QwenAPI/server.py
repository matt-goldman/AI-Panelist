# coding=utf-8
"""
FastAPI server for Qwen3-TTS.

Provides a simple REST API for text-to-speech synthesis.
Supports both CustomVoice (preset speakers) and VoiceClone (custom .pt files) modes.

See setup-guide.md for installation and usage instructions.

Configuration via environment variables:
    TTS_MODE: "custom_voice" (default) or "voice_clone"
    TTS_MODEL: Model name/path (defaults based on mode)
    TTS_DEVICE: "cuda:0" (default) or "cpu"
    TTS_VOICE_PROMPT: Path to default .pt voice prompt file (for voice_clone mode)
"""

import io
import os
import base64
import logging
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Optional, Dict

import torch
import soundfile as sf
from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel

# Configure logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

# ============================================================================
# Configuration (via environment variables or defaults)
# ============================================================================

# Mode: "custom_voice" uses preset speakers, "voice_clone" uses .pt files
TTS_MODE = os.environ.get("TTS_MODE", "voice_clone").lower()

# Model selection based on mode
if TTS_MODE == "voice_clone":
    DEFAULT_MODEL = "Qwen/Qwen3-TTS-12Hz-1.7B-Base"
else:
    DEFAULT_MODEL = "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice"

TTS_MODEL = os.environ.get("TTS_MODEL", DEFAULT_MODEL)
TTS_DEVICE = os.environ.get("TTS_DEVICE", "cuda:0")
TTS_VOICE_PROMPT = os.environ.get("TTS_VOICE_PROMPT", "")  # Path to default .pt file

# Attention implementation: "flash_attention_2", "sdpa", or "eager"
# - flash_attention_2: Fastest, but requires flash-attn package (hard on Windows)
# - sdpa: PyTorch native scaled dot product attention, good fallback (PyTorch 2.0+)
# - eager: Standard attention, slowest but always works
TTS_ATTN_IMPL = os.environ.get("TTS_ATTN_IMPL", "sdpa")

# ============================================================================
# Global state
# ============================================================================

model = None
default_voice_prompt = None  # VoiceClonePromptItem loaded at startup
voice_prompt_cache: Dict[str, object] = {}  # Cache for loaded .pt files


# ============================================================================
# Request/Response models
# ============================================================================

class TTSRequest(BaseModel):
    """Request body for TTS synthesis (CustomVoice mode)."""
    text: str
    language: str = "Auto"
    speaker: str = "Vivian"
    instruct: Optional[str] = None
    output_format: str = "wav"  # "wav" or "base64"


class VoiceCloneRequest(BaseModel):
    """Request body for TTS synthesis with cloned voice."""
    text: str
    language: str = "Auto"
    voice_prompt_path: Optional[str] = None  # Path to .pt file, uses default if not specified
    output_format: str = "wav"  # "wav" or "base64"


class TTSResponse(BaseModel):
    """Response for base64 output format."""
    audio: str
    sample_rate: int


# ============================================================================
# Voice prompt loading
# ============================================================================

def load_voice_prompt(pt_path: str, device: str = "cpu"):
    """Load a voice clone prompt from a .pt file."""
    from qwen_tts import VoiceClonePromptItem
    
    path = Path(pt_path)
    if not path.exists():
        raise FileNotFoundError(f"Voice prompt file not found: {pt_path}")
    
    logger.info(f"Loading voice prompt from: {pt_path}")
    data = torch.load(pt_path, map_location=device, weights_only=False)
    
    # Handle different .pt file formats
    if isinstance(data, VoiceClonePromptItem):
        # Already a VoiceClonePromptItem - move tensors to device
        if device != "cpu":
            if data.ref_code is not None:
                data.ref_code = data.ref_code.to(device)
            if data.ref_spk_embedding is not None:
                data.ref_spk_embedding = data.ref_spk_embedding.to(device)
        return data
    elif isinstance(data, dict):
        # Check for Gradio demo format: {"items": [<prompt_dict>]}
        if "items" in data and isinstance(data["items"], list) and len(data["items"]) > 0:
            prompt_data = data["items"][0]
        else:
            # Direct dictionary format
            prompt_data = data
        
        # Move tensors to device
        ref_code = prompt_data.get('ref_code')
        ref_spk_embedding = prompt_data['ref_spk_embedding']
        
        if device != "cpu":
            if ref_code is not None:
                ref_code = ref_code.to(device)
            if ref_spk_embedding is not None:
                ref_spk_embedding = ref_spk_embedding.to(device)
        
        return VoiceClonePromptItem(
            ref_code=ref_code,
            ref_spk_embedding=ref_spk_embedding,
            x_vector_only_mode=prompt_data.get('x_vector_only_mode', False),
            icl_mode=prompt_data.get('icl_mode', True),
            ref_text=prompt_data.get('ref_text'),
        )
    else:
        raise ValueError(f"Unknown voice prompt format: {type(data)}")


def get_voice_prompt(pt_path: Optional[str] = None, device: str = "cpu"):
    """Get a voice prompt, using cache for efficiency."""
    global voice_prompt_cache, default_voice_prompt
    
    if pt_path is None:
        if default_voice_prompt is None:
            raise HTTPException(
                status_code=400, 
                detail="No voice_prompt_path specified and no default voice configured. "
                       "Set TTS_VOICE_PROMPT environment variable or provide voice_prompt_path in request."
            )
        return default_voice_prompt
    
    # Check cache
    if pt_path in voice_prompt_cache:
        return voice_prompt_cache[pt_path]
    
    # Load and cache (on GPU for faster inference)
    prompt = load_voice_prompt(pt_path, device=device)
    voice_prompt_cache[pt_path] = prompt
    return prompt


# ============================================================================
# Startup/shutdown
# ============================================================================

@asynccontextmanager
async def lifespan(app: FastAPI):
    """Load model at startup, cleanup at shutdown."""
    global model, default_voice_prompt
    
    logger.info(f"Starting Qwen3-TTS server in {TTS_MODE} mode...")
    logger.info(f"Model: {TTS_MODEL}")
    logger.info(f"Attention: {TTS_ATTN_IMPL}")
    logger.info("This may take a few minutes on first run (downloading model weights)...")
    
    from qwen_tts import Qwen3TTSModel
    
    device = TTS_DEVICE
    dtype = torch.bfloat16
    
    # Check CUDA availability
    if device.startswith("cuda") and not torch.cuda.is_available():
        logger.warning("CUDA not available, falling back to CPU (this will be slower)")
        device = "cpu"
        dtype = torch.float32
    
    # Enable CUDA optimizations
    if device.startswith("cuda"):
        torch.backends.cudnn.benchmark = True
        logger.info("CUDA optimizations enabled")
    
    try:
        # Determine attention implementation
        attn_impl = TTS_ATTN_IMPL if TTS_ATTN_IMPL != "eager" else None
        
        model = Qwen3TTSModel.from_pretrained(
            TTS_MODEL,
            device_map=device,
            dtype=dtype,
            attn_implementation=attn_impl,
        )
        logger.info(f"Model loaded successfully on {device}")
        
        # Load default voice prompt if configured (load directly to GPU)
        if TTS_VOICE_PROMPT:
            try:
                default_voice_prompt = load_voice_prompt(TTS_VOICE_PROMPT, device=device)
                logger.info(f"Default voice prompt loaded: {TTS_VOICE_PROMPT}")
            except Exception as e:
                logger.warning(f"Failed to load default voice prompt: {e}")
        
    except Exception as e:
        logger.error(f"Failed to load model: {e}")
        raise
    
    yield
    
    # Cleanup
    logger.info("Shutting down...")
    model = None
    default_voice_prompt = None
    voice_prompt_cache.clear()


# ============================================================================
# FastAPI app
# ============================================================================

app = FastAPI(
    title="Qwen3-TTS API",
    description="Local TTS API using Qwen3-TTS. Supports CustomVoice and VoiceClone modes.",
    version="1.1.0",
    lifespan=lifespan,
)


@app.get("/health")
async def health():
    """Health check endpoint."""
    return {
        "status": "ok",
        "mode": TTS_MODE,
        "model_loaded": model is not None,
        "default_voice_loaded": default_voice_prompt is not None,
    }


@app.get("/config")
async def get_config():
    """Get current server configuration."""
    return {
        "mode": TTS_MODE,
        "model": TTS_MODEL,
        "device": TTS_DEVICE,
        "attention": TTS_ATTN_IMPL,
        "default_voice_prompt": TTS_VOICE_PROMPT or None,
        "cached_voice_prompts": list(voice_prompt_cache.keys()),
    }


@app.get("/speakers")
async def list_speakers():
    """List available speakers (CustomVoice mode only)."""
    if model is None:
        raise HTTPException(status_code=503, detail="Model not loaded")
    
    if TTS_MODE != "custom_voice":
        return {"speakers": [], "note": "Speakers only available in custom_voice mode. Using voice_clone mode."}
    
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
async def synthesize(request: VoiceCloneRequest):
    """
    Synthesize speech from text using cloned voice (voice_clone mode).
    
    If no voice_prompt_path is specified, uses the default voice configured via TTS_VOICE_PROMPT.
    
    Returns WAV audio bytes by default, or base64-encoded audio if output_format="base64".
    """
    if model is None:
        raise HTTPException(status_code=503, detail="Model not loaded")
    
    if TTS_MODE != "voice_clone":
        raise HTTPException(
            status_code=400, 
            detail="Server is in custom_voice mode. Use /tts/custom endpoint or restart with TTS_MODE=voice_clone"
        )
    
    if not request.text or not request.text.strip():
        raise HTTPException(status_code=400, detail="Text is required")
    
    try:
        voice_prompt = get_voice_prompt(request.voice_prompt_path, device=TTS_DEVICE)
        
        logger.info(f"Synthesizing: '{request.text[:50]}...' with cloned voice")
        
        # Use inference_mode for faster generation (no gradient tracking)
        with torch.inference_mode():
            wavs, sr = model.generate_voice_clone(
                text=request.text.strip(),
                language=request.language,
                voice_clone_prompt=[voice_prompt],  # Must be a list
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
            
    except FileNotFoundError as e:
        raise HTTPException(status_code=404, detail=str(e))
    except Exception as e:
        logger.error(f"Synthesis failed: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@app.post("/tts/custom", response_class=Response)
async def synthesize_custom_voice(request: TTSRequest):
    """
    Synthesize speech from text using preset speakers (custom_voice mode).
    
    Returns WAV audio bytes by default, or base64-encoded audio if output_format="base64".
    """
    if model is None:
        raise HTTPException(status_code=503, detail="Model not loaded")
    
    if TTS_MODE != "custom_voice":
        raise HTTPException(
            status_code=400, 
            detail="Server is in voice_clone mode. Use /tts endpoint or restart with TTS_MODE=custom_voice"
        )
    
    if not request.text or not request.text.strip():
        raise HTTPException(status_code=400, detail="Text is required")
    
    try:
        logger.info(f"Synthesizing: '{request.text[:50]}...' with speaker={request.speaker}")
        
        # Use inference_mode for faster generation (no gradient tracking)
        with torch.inference_mode():
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
