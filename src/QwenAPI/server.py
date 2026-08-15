"""
Optimized Qwen3-TTS REST API Server

Uses dffdeeq/Qwen3-TTS-streaming fork with:
- torch.compile with max-autotune
- Fast codebook generation
- Flash Attention 2

Configuration via environment variables:
    TTS_REF_AUDIO: Path to reference audio file for voice cloning (default: ~/qwentts/bubbles-sample.wav)
    TTS_REF_TEXT: Transcript of the reference audio (required for voice cloning)

TODO: Add fallback to built-in voices when no reference audio is provided.
TODO: Support loading pre-saved .pt voice prompt files for faster startup (currently recreates from audio on each start).
"""

import os
import io
import time
import torch
import soundfile as sf
from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field
from typing import Optional

# Enable TensorFloat32 for better performance on Ampere+ GPUs
torch.set_float32_matmul_precision('high')

# Enable torch.compile cache to speed up subsequent startups
# Cache is stored at ~/.cache/torch_inductor/
os.environ.setdefault("TORCHINDUCTOR_CACHE_DIR", os.path.expanduser("~/.cache/torch_inductor"))
os.environ.setdefault("TORCHINDUCTOR_FX_GRAPH_CACHE", "1")
torch._inductor.config.fx_graph_cache = True

app = FastAPI(title="Qwen3-TTS Optimized API")

# ============================================================================
# Configuration - Voice cloning reference audio
# ============================================================================

# Path to reference audio (use ~ for home directory)
TTS_REF_AUDIO = os.path.expanduser(
    os.environ.get("TTS_REF_AUDIO", "~/qwentts/bubbles-sample.wav")
)

# Transcript of the reference audio - MUST match the audio content
TTS_REF_TEXT = os.environ.get(
    "TTS_REF_TEXT",
    "Have you or your users ever accidentally deleted critical system information? "
    "So of course there are edge cases to this. So with this area just a little bit "
    "of effort can go a long way in making sure that the user can properly interpret "
    "the data that you're trying to convey."
)

# Global model reference
model = None
voice_prompt = None
is_ready = False


class TTSRequest(BaseModel):
    text: str = Field(..., description="Text to synthesize")
    language: str = Field(default="English", description="Language: English, Chinese, etc.")


class HealthResponse(BaseModel):
    status: str
    ready: bool
    rtf_expected: float = Field(description="Expected real-time factor (lower is faster)")


def load_model():
    """Load model and enable optimizations."""
    global model, voice_prompt, is_ready

    print("=" * 60)
    print("Loading Qwen3-TTS-12Hz-1.7B-Base...")
    print("=" * 60)

    from qwen_tts import Qwen3TTSModel

    start = time.time()
    model = Qwen3TTSModel.from_pretrained(
        "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
        device_map="cuda:0",
        dtype=torch.bfloat16,
        attn_implementation="flash_attention_2",
    )
    print(f"[{time.time() - start:.2f}s] Model loaded")

    # Create voice clone prompt from reference audio
    start = time.time()
    print(f"Reference audio: {TTS_REF_AUDIO}")
    
    if not os.path.exists(TTS_REF_AUDIO):
        raise FileNotFoundError(
            f"Reference audio not found: {TTS_REF_AUDIO}\n"
            f"Set TTS_REF_AUDIO environment variable or place your audio file at the default path."
        )

    # Create prompt using the new library (handles tokenization properly)
    voice_prompt = model.create_voice_clone_prompt(
        ref_audio=TTS_REF_AUDIO,
        ref_text=TTS_REF_TEXT,
        x_vector_only_mode=False,  # Use full ICL for accent preservation
    )
    print(f"[{time.time() - start:.2f}s] Voice prompt created from reference audio (ICL mode)")

    # Enable optimizations (if available in this version)
    print("\nEnabling optimizations...")
    try:
        model.enable_streaming_optimizations(
            decode_window_frames=300,  # Larger window for non-streaming
            use_compile=True,
            use_cuda_graphs=False,  # Variable input sizes
            compile_mode="max-autotune",
            use_fast_codebook=True,
            compile_codebook_predictor=True,
            compile_talker=True,
        )
    except AttributeError:
        print("  Note: enable_streaming_optimizations not available in this qwen-tts version")

    # Warmup runs (compilation happens here)
    print("\nWarmup runs (this will take ~2 minutes for compilation)...")
    warmup_texts = [
        "Testing one two three four five.",
        "Hello, how are you? This is a warmup test.",
        "Third warmup run to fully compile all model components.",
    ]

    for i, text in enumerate(warmup_texts, 1):
        start = time.time()
        wavs, sr = model.generate_voice_clone(
            text=text,
            language="English",
            voice_clone_prompt=voice_prompt,
        )
        audio_dur = len(wavs[0]) / sr if wavs else 0
        elapsed = time.time() - start
        rtf = elapsed / audio_dur if audio_dur > 0 else 0
        print(f"  Warmup {i}: {elapsed:.2f}s, Audio: {audio_dur:.2f}s, RTF: {rtf:.2f}")

    is_ready = True
    print("\n" + "=" * 60)
    print("Server ready! RTF should be ~0.3")
    print("=" * 60)


@app.on_event("startup")
async def startup():
    load_model()


@app.get("/health", response_model=HealthResponse)
async def health(response: Response):
    """Health check endpoint.

    Returns 503 until the model has compiled and warmed up, so orchestrators
    (Aspire's WaitFor) block on a genuinely usable server rather than an open port.
    """
    if not is_ready:
        response.status_code = 503

    return HealthResponse(
        status="ok" if is_ready else "warming_up",
        ready=is_ready,
        rtf_expected=0.32,
    )


@app.post("/tts")
async def synthesize(request: TTSRequest):
    """
    Synthesize speech from text using voice cloning.

    Returns WAV audio as binary response.
    """
    if not is_ready:
        raise HTTPException(status_code=503, detail="Model still warming up")

    start = time.time()

    try:
        wavs, sr = model.generate_voice_clone(
            text=request.text,
            language=request.language,
            voice_clone_prompt=voice_prompt,
        )
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

    if not wavs or len(wavs[0]) == 0:
        raise HTTPException(status_code=500, detail="No audio generated")

    audio = wavs[0]
    audio_duration = len(audio) / sr
    total_time = time.time() - start
    rtf = total_time / audio_duration

    print(f"TTS: '{request.text[:50]}...' | {total_time:.2f}s | Audio: {audio_duration:.2f}s | RTF: {rtf:.2f}")

    # Encode as WAV
    buffer = io.BytesIO()
    sf.write(buffer, audio, sr, format='WAV')
    buffer.seek(0)

    return Response(
        content=buffer.read(),
        media_type="audio/wav",
        headers={
            "X-Audio-Duration": str(audio_duration),
            "X-Generation-Time": str(total_time),
            "X-RTF": str(rtf),
        }
    )


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)