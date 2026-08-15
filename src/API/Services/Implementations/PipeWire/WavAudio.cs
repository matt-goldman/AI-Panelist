using System.Buffers.Binary;
using System.Text;
using API.Services.Interfaces;

namespace API.Services.Implementations.PipeWire;

/// <summary>
/// Minimal RIFF/WAVE reader. We only need enough to hand raw PCM plus its format to
/// pw-cat — the TTS server returns plain PCM WAV, so a full decoder would be overkill.
/// </summary>
internal static class WavAudio
{
    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    public sealed record WavData(AudioStreamFormat Format, ushort FormatTag, ReadOnlyMemory<byte> Pcm)
    {
        /// <summary>
        /// The pw-cat --format name for this data.
        /// </summary>
        public string PwCatFormat => FormatTag == FormatIeeeFloat
            ? Format.BitsPerSample == 64 ? "f64" : "f32"
            : Format.BitsPerSample switch
            {
                8  => "u8",
                24 => "s24",
                32 => "s32",
                _  => "s16"
            };
    }

    /// <summary>
    /// Parse a WAV file held in memory.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a WAV file we can play.</exception>
    public static WavData Parse(ReadOnlyMemory<byte> wav)
    {
        var span = wav.Span;

        if (span.Length < 12
            || Encoding.ASCII.GetString(span[..4]) != "RIFF"
            || Encoding.ASCII.GetString(span[8..12]) != "WAVE")
        {
            throw new InvalidDataException("Audio data is not a RIFF/WAVE stream.");
        }

        ushort formatTag = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        var haveFormat = false;

        var offset = 12;
        while (offset + 8 <= span.Length)
        {
            var chunkId = Encoding.ASCII.GetString(span.Slice(offset, 4));
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4));
            var chunkStart = offset + 8;

            if (chunkStart + chunkSize > span.Length)
            {
                // Truncated or streaming-style size; clamp to what we actually have.
                chunkSize = (uint)(span.Length - chunkStart);
            }

            if (chunkId == "fmt " && chunkSize >= 16)
            {
                var fmt = span.Slice(chunkStart, (int)chunkSize);
                formatTag     = BinaryPrimitives.ReadUInt16LittleEndian(fmt[..2]);
                channels      = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(2, 2));
                sampleRate    = BinaryPrimitives.ReadUInt32LittleEndian(fmt.Slice(4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(14, 2));

                if (formatTag == FormatExtensible && chunkSize >= 40)
                {
                    // The real format tag is the first two bytes of the GUID subformat.
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(24, 2));
                }

                haveFormat = true;
            }
            else if (chunkId == "data")
            {
                if (!haveFormat)
                {
                    throw new InvalidDataException("WAV data chunk appeared before its fmt chunk.");
                }

                if (formatTag is not (FormatPcm or FormatIeeeFloat))
                {
                    throw new InvalidDataException($"Unsupported WAV format tag {formatTag}; only PCM and IEEE float are supported.");
                }

                return new WavData(
                    new AudioStreamFormat((int)sampleRate, channels, bitsPerSample),
                    formatTag,
                    wav.Slice(chunkStart, (int)chunkSize));
            }

            // Chunks are word-aligned.
            offset = chunkStart + (int)chunkSize + ((int)chunkSize % 2);
        }

        throw new InvalidDataException("WAV stream contained no data chunk.");
    }
}
