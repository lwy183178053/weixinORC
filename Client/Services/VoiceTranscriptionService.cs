using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Client.Services
{
    public sealed class VoiceTranscriptionResult
    {
        public bool Success { get; init; }
        public string Text { get; init; } = string.Empty;
        public string EngineLabel { get; init; } = string.Empty;
        public string ErrorMessage { get; init; } = string.Empty;
    }

    /// <summary>
    /// Lightweight 2-tier voice-to-text service:
    /// 1) Server AI gateway transcription (/api/v1/ai/transcribe -> Whisper or multimodal input_audio)
    /// 2) 100% offline local Windows built-in zh-CN speech recognition fallback (zero extra model downloads).
    /// </summary>
    public static partial class VoiceTranscriptionService
    {
        [GeneratedRegex(@"^(🎤\s*\[语音(?:\s*\d+""?)?\])(.*)$", RegexOptions.Singleline | RegexOptions.Compiled)]
        private static partial Regex VoiceHeaderRegex();

        public static string MergeTranscriptIntoContent(string existingContent, string transcript)
        {
            string cleanTranscript = (transcript ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            string raw = (existingContent ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            var m = VoiceHeaderRegex().Match(raw);
            string header = m.Success ? m.Groups[1].Value.Trim() : "🎤 [语音]";
            if (string.IsNullOrEmpty(cleanTranscript)) return header;
            return $"{header}\n{cleanTranscript}";
        }

        public static bool HasSavedTranscript(string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;
            var m = VoiceHeaderRegex().Match(content.Trim());
            return m.Success && !string.IsNullOrWhiteSpace(m.Groups[2].Value);
        }

        public static async Task<VoiceTranscriptionResult> TranscribeAsync(string audioUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(audioUrl))
            {
                return new VoiceTranscriptionResult
                {
                    Success = false,
                    ErrorMessage = "该条语音暂无音频文件，请先点击「刷新同步」获取语音。"
                };
            }

            // Tier 1: Cloud AI transcription (high accuracy for Chinese speech & punctuation)
            string? cloudErr = null;
            try
            {
                var apiRes = await AppServices.Backend.TranscribeChatAudioAsync(audioUrl, ct);
                if (apiRes.IsSuccess && apiRes.Data is not null && !string.IsNullOrWhiteSpace(apiRes.Data.Text))
                {
                    return new VoiceTranscriptionResult
                    {
                        Success = true,
                        Text = apiRes.Data.Text.Trim(),
                        EngineLabel = "智能语音识别"
                    };
                }
                cloudErr = apiRes.ErrorMessage;
            }
            catch (Exception ex)
            {
                cloudErr = ex.Message;
            }

            // Tier 2: Local Windows built-in zh-CN offline recognizer fallback
            try
            {
                byte[]? audioBytes = await AppServices.Backend.DownloadChatAudioBytesAsync(audioUrl, ct);
                if (audioBytes is null || audioBytes.Length < 44)
                {
                    return new VoiceTranscriptionResult
                    {
                        Success = false,
                        ErrorMessage = string.IsNullOrWhiteSpace(cloudErr)
                            ? "未能下载语音文件。"
                            : $"转文字失败：{cloudErr}"
                    };
                }

                byte[] validWav = EnsureValidWavHeader(audioBytes);
                var (localOk, localText, localErr) = await TryWindowsOfflineSpeechAsync(validWav, ct);
                if (localOk && !string.IsNullOrWhiteSpace(localText))
                {
                    return new VoiceTranscriptionResult
                    {
                        Success = true,
                        Text = localText.Trim(),
                        EngineLabel = "本机离线识别"
                    };
                }

                string detail = !string.IsNullOrWhiteSpace(localErr) ? localErr : (cloudErr ?? "未识别出清晰语音");
                return new VoiceTranscriptionResult
                {
                    Success = false,
                    ErrorMessage = $"语音转文字失败：{detail}"
                };
            }
            catch (OperationCanceledException)
            {
                return new VoiceTranscriptionResult { Success = false, ErrorMessage = "已取消语音转文字。" };
            }
            catch (Exception ex)
            {
                return new VoiceTranscriptionResult
                {
                    Success = false,
                    ErrorMessage = $"语音转文字异常：{ex.Message}"
                };
            }
        }

        private static async Task<(bool Ok, string Text, string Error)> TryWindowsOfflineSpeechAsync(byte[] wavBytes, CancellationToken ct)
        {
            string tempWav = Path.Combine(Path.GetTempPath(), $"wx_stt_{Guid.NewGuid():N}.wav");
            try
            {
                await File.WriteAllBytesAsync(tempWav, wavBytes, ct);

                const string psScript = @"
param([string]$WavPath)
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
try {
    Add-Type -AssemblyName System.Speech
    $recognizers = [System.Speech.Recognition.SpeechRecognitionEngine]::InstalledRecognizers()
    $target = $recognizers | Where-Object { $_.Culture.Name -like 'zh*' } | Select-Object -First 1
    if ($null -eq $target) {
        $target = $recognizers | Select-Object -First 1
    }
    if ($null -eq $target) {
        Write-Error '本机未安装 Windows 离线语音识别包'
        exit 2
    }
    $engine = New-Object System.Speech.Recognition.SpeechRecognitionEngine($target.Culture)
    $engine.LoadGrammar((New-Object System.Speech.Recognition.DictationGrammar))
    $engine.SetInputToWaveFile($WavPath)
    $parts = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt 40; $i++) {
        $r = $engine.Recognize()
        if ($null -eq $r) { break }
        if (-not [string]::IsNullOrWhiteSpace($r.Text)) {
            $parts.Add($r.Text.Trim())
        }
    }
    $engine.Dispose()
    [Console]::Write(($parts -join ''))
} catch {
    Write-Error $_.Exception.Message
    exit 1
}
";
                string encodedCmd = Convert.ToBase64String(Encoding.Unicode.GetBytes(
                    $"& {{ {psScript} }} -WavPath '{tempWav.Replace("'", "''")}'"));

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCmd}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var proc = Process.Start(psi);
                if (proc is null)
                {
                    return (false, string.Empty, "无法启动本机语音识别进程");
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));

                Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                Task<string> stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
                await proc.WaitForExitAsync(timeoutCts.Token);

                string text = (await stdoutTask).Trim();
                string err = (await stderrTask).Trim();

                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(text))
                {
                    return (true, text, string.Empty);
                }
                if (proc.ExitCode == 0)
                {
                    return (false, string.Empty, "未识别出有效人声内容");
                }
                return (false, string.Empty, string.IsNullOrWhiteSpace(err) ? "本机离线识别未返回结果" : err);
            }
            catch (OperationCanceledException)
            {
                return (false, string.Empty, "本机离线语音识别超时");
            }
            catch (Exception ex)
            {
                return (false, string.Empty, ex.Message);
            }
            finally
            {
                try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
            }
        }

        private static byte[] EnsureValidWavHeader(byte[] raw)
        {
            // Repair legacy 42-byte broken header ("RIFF...WAVEfmt\0") into standard 44-byte WAV
            if (raw.Length > 42 &&
                raw[0] == 'R' && raw[1] == 'I' && raw[2] == 'F' && raw[3] == 'F' &&
                raw[8] == 'W' && raw[9] == 'A' && raw[10] == 'V' && raw[11] == 'E' &&
                raw[12] == 'f' && raw[13] == 'm' && raw[14] == 't' && raw[15] == 0x00 &&
                raw[34] == 'd' && raw[35] == 'a' && raw[36] == 't' && raw[37] == 'a')
            {
                int pcmLen = raw.Length - 42;
                int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(24, 4));
                if (sampleRate < 8000 || sampleRate > 48000) sampleRate = 24000;
                short channels = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(22, 2));
                if (channels < 1 || channels > 2) channels = 1;
                short blockAlign = (short)(channels * 2);
                int byteRate = sampleRate * blockAlign;

                byte[] fixedWav = new byte[44 + pcmLen];
                fixedWav[0] = (byte)'R'; fixedWav[1] = (byte)'I'; fixedWav[2] = (byte)'F'; fixedWav[3] = (byte)'F';
                BinaryPrimitives.WriteInt32LittleEndian(fixedWav.AsSpan(4, 4), 36 + pcmLen);
                fixedWav[8] = (byte)'W'; fixedWav[9] = (byte)'A'; fixedWav[10] = (byte)'V'; fixedWav[11] = (byte)'E';
                fixedWav[12] = (byte)'f'; fixedWav[13] = (byte)'m'; fixedWav[14] = (byte)'t'; fixedWav[15] = (byte)' ';
                BinaryPrimitives.WriteInt32LittleEndian(fixedWav.AsSpan(16, 4), 16);
                BinaryPrimitives.WriteInt16LittleEndian(fixedWav.AsSpan(20, 2), 1);
                BinaryPrimitives.WriteInt16LittleEndian(fixedWav.AsSpan(22, 2), channels);
                BinaryPrimitives.WriteInt32LittleEndian(fixedWav.AsSpan(24, 4), sampleRate);
                BinaryPrimitives.WriteInt32LittleEndian(fixedWav.AsSpan(28, 4), byteRate);
                BinaryPrimitives.WriteInt16LittleEndian(fixedWav.AsSpan(32, 2), blockAlign);
                BinaryPrimitives.WriteInt16LittleEndian(fixedWav.AsSpan(34, 2), 16);
                fixedWav[36] = (byte)'d'; fixedWav[37] = (byte)'a'; fixedWav[38] = (byte)'t'; fixedWav[39] = (byte)'a';
                BinaryPrimitives.WriteInt32LittleEndian(fixedWav.AsSpan(40, 4), pcmLen);
                Buffer.BlockCopy(raw, 42, fixedWav, 44, pcmLen);
                return fixedWav;
            }
            return raw;
        }
    }
}
