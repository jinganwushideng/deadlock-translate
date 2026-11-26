using System.Buffers.Binary;
using System.Text;

namespace DeadlockVoiceLab.Audio;

public readonly record struct DecodedWav(float[] Left, float[] Right, int SampleRate, bool IsStereo);

/// <summary>Pure C# RIFF/WAVE encoder and reader (PCM 8/16/24/32 and IEEE Float 32).</summary>
public static class WavCodec
{
    public static byte[] EncodePcm16Mono(ReadOnlySpan<float> samples, int sampleRate = 16000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        int dataBytes = checked(samples.Length * 2);
        var buffer = new byte[44 + dataBytes];
        WriteHeader(buffer, channels: 1, sampleRate: sampleRate, bitsPerSample: 16, dataBytes: dataBytes);

        int offset = 44;
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            if (!float.IsFinite(s)) s = 0f;
            s = Math.Clamp(s, -1f, 1f);
            short pcm = (short)Math.Round(s * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset, 2), pcm);
            offset += 2;
        }
        return buffer;
    }

    public static byte[] EncodePcm16Stereo(ReadOnlySpan<float> interleavedStereo, int sampleRate = 48000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (interleavedStereo.Length % 2 != 0) throw new ArgumentException("Stereo buffer must have even sample count.");
        int dataBytes = checked(interleavedStereo.Length * 2);
        var buffer = new byte[44 + dataBytes];
        WriteHeader(buffer, channels: 2, sampleRate: sampleRate, bitsPerSample: 16, dataBytes: dataBytes);

        int offset = 44;
        for (int i = 0; i < interleavedStereo.Length; i++)
        {
            float s = interleavedStereo[i];
            if (!float.IsFinite(s)) s = 0f;
            s = Math.Clamp(s, -1f, 1f);
            short pcm = (short)Math.Round(s * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset, 2), pcm);
            offset += 2;
        }
        return buffer;
    }

    public static DecodedWav Decode(ReadOnlySpan<byte> wavBytes)
    {
        if (wavBytes.Length < 12) throw new InvalidDataException("WAV file too small.");
        if (!wavBytes[..4].SequenceEqual("RIFF"u8) || !wavBytes.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Invalid RIFF/WAVE header.");

        int offset = 12;
        bool hasFmt = false;
        ushort formatTag = 0;
        ushort channels = 0;
        int sampleRate = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;
        ReadOnlySpan<byte> dataSpan = default;
        bool hasData = false;

        while (offset + 8 <= wavBytes.Length)
        {
            var chunkId = Encoding.ASCII.GetString(wavBytes.Slice(offset, 4));
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wavBytes.Slice(offset + 4, 4));
            offset += 8;

            int available = wavBytes.Length - offset;
            int readSize = (int)Math.Min(chunkSize, (uint)available);
            var chunk = wavBytes.Slice(offset, readSize);

            if (chunkId == "fmt ")
            {
                if (chunk.Length < 16) throw new InvalidDataException("Truncated WAV fmt chunk.");
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(chunk[..2]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(4, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(12, 2));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(14, 2));

                if (formatTag == 0xFFFE && chunk.Length >= 40)
                {
                    // WAVE_FORMAT_EXTENSIBLE: SubFormat GUID starts at offset 24
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(24, 2));
                }
                hasFmt = true;
            }
            else if (chunkId == "data")
            {
                dataSpan = chunk;
                hasData = true;
            }

            long advance = chunkSize + (chunkSize & 1);
            if (offset + advance > wavBytes.Length) break;
            offset += (int)advance;
        }

        if (!hasFmt || !hasData) throw new InvalidDataException("Missing fmt or data chunk in WAV.");
        if (channels == 0 || sampleRate <= 0 || blockAlign == 0)
            throw new InvalidDataException("Invalid WAV stream metadata.");
        if (formatTag is not (1 or 3))
            throw new InvalidDataException($"Unsupported WAV format tag {formatTag} (only PCM=1 and IEEE Float=3 supported).");

        int bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample == 0 || channels * bytesPerSample > blockAlign)
            throw new InvalidDataException($"Invalid WAV bit depth {bitsPerSample}.");
        if (formatTag == 3 && bitsPerSample != 32)
            throw new InvalidDataException($"Unsupported IEEE Float bit depth {bitsPerSample}.");
        if (formatTag == 1 && bitsPerSample is not (8 or 16 or 24 or 32))
            throw new InvalidDataException($"Unsupported PCM bit depth {bitsPerSample}.");

        int frames = dataSpan.Length / blockAlign;
        var left = new float[frames];
        bool isStereo = channels >= 2;
        var right = isStereo ? new float[frames] : new float[frames];

        for (int f = 0; f < frames; f++)
        {
            int frameOffset = f * blockAlign;
            float l = ReadSample(dataSpan.Slice(frameOffset, bytesPerSample), formatTag, bitsPerSample);
            left[f] = l;
            if (isStereo)
            {
                right[f] = ReadSample(dataSpan.Slice(frameOffset + bytesPerSample, bytesPerSample), formatTag, bitsPerSample);
            }
            else
            {
                right[f] = l;
            }
        }

        return new DecodedWav(left, right, sampleRate, isStereo);
    }

    private static float ReadSample(ReadOnlySpan<byte> bytes, ushort formatTag, ushort bitsPerSample)
    {
        if (formatTag == 3)
        {
            float v = BinaryPrimitives.ReadSingleLittleEndian(bytes);
            return float.IsFinite(v) ? Math.Clamp(v, -1f, 1f) : 0f;
        }

        return bitsPerSample switch
        {
            8 => (bytes[0] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
            24 => ((bytes[0] | (bytes[1] << 8) | ((sbyte)bytes[2] << 16)) / 8388608f),
            32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
            _ => 0f
        };
    }

    private static void WriteHeader(Span<byte> header, ushort channels, int sampleRate, ushort bitsPerSample, int dataBytes)
    {
        "RIFF"u8.CopyTo(header[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), 36 + dataBytes);
        "WAVE"u8.CopyTo(header.Slice(8, 4));
        "fmt "u8.CopyTo(header.Slice(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(20, 2), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24, 4), sampleRate);
        int byteRate = checked(sampleRate * channels * (bitsPerSample / 8));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28, 4), byteRate);
        ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(32, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(34, 2), bitsPerSample);
        "data"u8.CopyTo(header.Slice(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(40, 4), dataBytes);
    }
}
