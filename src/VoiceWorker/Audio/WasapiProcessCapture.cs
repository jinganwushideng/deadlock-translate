using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeadlockVoiceLab.Audio;

public sealed record AudioProcessInfo(
    int Pid,
    string Name,
    string WindowTitle,
    bool IsDeadlock,
    bool IsCommonAudioApp);

public sealed class WasapiProcessCapture : IDisposable
{
    private const uint CLSCTX_ALL = 23;
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x00000002;
    private const ushort WAVE_FORMAT_PCM = 1;
    private const ushort WAVE_FORMAT_IEEE_FLOAT = 3;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private static readonly HashSet<string> PriorityProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "deadlock", "discord", "kook", "chrome", "msedge", "firefox", "brave", "opera", "obs64", "vlc", "steamwebhelper"
    };

    private CancellationTokenSource? cts;
    private Thread? workerThread;
    private volatile bool disposed;

    public bool IsRunning => workerThread is { IsAlive: true };

    public static AudioProcessInfo[] ListProcesses()
    {
        var results = new List<AudioProcessInfo>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var p in processes)
            {
                try
                {
                    if (p.Id <= 4) continue;
                    string name = p.ProcessName;
                    string title = "";
                    try { title = p.MainWindowTitle ?? ""; } catch { }

                    bool isDeadlock = name.Equals("deadlock", StringComparison.OrdinalIgnoreCase) ||
                                      name.Equals("citadel", StringComparison.OrdinalIgnoreCase);
                    bool isCommon = PriorityProcessNames.Contains(name);

                    if (!isDeadlock && !isCommon && string.IsNullOrWhiteSpace(title))
                        continue;

                    results.Add(new AudioProcessInfo(p.Id, name, title.Trim(), isDeadlock, isCommon));
                }
                catch
                {
                    // Ignore exited or inaccessible processes
                }
            }
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }

        return results
            .OrderByDescending(p => p.IsDeadlock)
            .ThenByDescending(p => p.IsCommonAudioApp && !string.IsNullOrWhiteSpace(p.WindowTitle))
            .ThenByDescending(p => p.IsCommonAudioApp)
            .ThenByDescending(p => !string.IsNullOrWhiteSpace(p.WindowTitle))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Pid)
            .Take(80)
            .ToArray();
    }

    public async Task StartAsync(
        string sourceMode,
        int? processId,
        Action<float[], bool> onStereo48kFrames,
        Action<string> onError,
        CancellationToken externalToken = default,
        string? microphoneId = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await StopAsync();

        cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var token = cts.Token;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        workerThread = new Thread(() =>
        {
            try
            {
                RunCaptureLoop(sourceMode, processId, ready, onStereo48kFrames, token, microphoneId);
            }
            catch (Exception ex)
            {
                if (!ready.TrySetException(ex))
                {
                    onError(ex.Message);
                }
            }
        })
        {
            IsBackground = true,
            Name = "VoiceLab-WASAPI-Capture",
            Priority = ThreadPriority.AboveNormal
        };

        // MTA avoids STA message-pumping stalls in ActivateAudioInterfaceAsync callbacks
        workerThread.SetApartmentState(ApartmentState.MTA);
        workerThread.Start();

        await ready.Task;
    }

    public async Task StopAsync()
    {
        var currentCts = cts;
        var currentThread = workerThread;
        cts = null;
        workerThread = null;

        if (currentCts != null)
        {
            try { currentCts.Cancel(); } catch { }
        }

        if (currentThread != null && currentThread.IsAlive)
        {
            await Task.Run(() => currentThread.Join(1500));
        }

        currentCts?.Dispose();
    }

    private static void RunCaptureLoop(
        string sourceMode,
        int? processId,
        TaskCompletionSource<bool> ready,
        Action<float[], bool> onStereo48kFrames,
        CancellationToken token,
        string? microphoneId)
    {
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        AutoResetEvent? sampleEvent = null;
        IntPtr formatPtr = IntPtr.Zero;
        bool isStereoSource = sourceMode != "mic";
        bool useEventCallback = sourceMode != "system";

        try
        {
            audioClient = sourceMode switch
            {
                "process" => ActivateProcessLoopback(processId ?? throw new ArgumentException("请选择要抓取的目标进程 PID。")),
                "system" => ActivateDefaultEndpoint(dataFlow: 0 /* eRender */),
                "mic" => ActivateDefaultEndpoint(dataFlow: 1 /* eCapture */, microphoneId),
                _ => throw new ArgumentException($"不支持的音频源模式: {sourceMode}")
            };

            uint streamFlags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            if (useEventCallback) streamFlags |= AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
            if (sourceMode is "process" or "system")
            {
                streamFlags |= AUDCLNT_STREAMFLAGS_LOOPBACK;
            }

            // Initialize as 48kHz Float32 (2ch for loopback, 1ch/2ch for mic fallback)
            ushort targetChannels = sourceMode == "mic" ? (ushort)1 : (ushort)2;
            ushort bitsPerSample = 32;
            ushort formatTag = WAVE_FORMAT_IEEE_FLOAT;

            formatPtr = AllocWaveFormatEx(formatTag, targetChannels, AudioDspPipeline.WorkingSampleRate, bitsPerSample);
            int hr = audioClient.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                streamFlags,
                hnsBufferDuration: 1_000_000, // 100ms in 100-ns units
                hnsPeriodicity: 0,
                pFormat: formatPtr,
                audioSessionGuid: IntPtr.Zero);

            if (hr < 0 && sourceMode == "mic")
            {
                Marshal.FreeHGlobal(formatPtr);
                targetChannels = 2;
                formatPtr = AllocWaveFormatEx(formatTag, targetChannels, AudioDspPipeline.WorkingSampleRate, bitsPerSample);
                hr = audioClient.Initialize(AUDCLNT_SHAREMODE_SHARED, streamFlags, 1_000_000, 0, formatPtr, IntPtr.Zero);
            }

            if (hr < 0)
            {
                // Fallback to 16-bit PCM 48kHz
                Marshal.FreeHGlobal(formatPtr);
                formatTag = WAVE_FORMAT_PCM;
                bitsPerSample = 16;
                targetChannels = sourceMode == "mic" ? (ushort)1 : (ushort)2;
                formatPtr = AllocWaveFormatEx(formatTag, targetChannels, AudioDspPipeline.WorkingSampleRate, bitsPerSample);
                hr = audioClient.Initialize(AUDCLNT_SHAREMODE_SHARED, streamFlags, 1_000_000, 0, formatPtr, IntPtr.Zero);
            }

            ThrowIfHrFailed(hr, $"IAudioClient.Initialize ({sourceMode})");

            if (useEventCallback)
            {
                sampleEvent = new AutoResetEvent(false);
                hr = audioClient.SetEventHandle(sampleEvent.SafeWaitHandle.DangerousGetHandle());
                ThrowIfHrFailed(hr, $"IAudioClient.SetEventHandle ({sourceMode})");
            }

            var captureIid = IID_IAudioCaptureClient;
            hr = audioClient.GetService(ref captureIid, out object captureObj);
            ThrowIfHrFailed(hr, $"IAudioClient.GetService ({sourceMode})");
            captureClient = (IAudioCaptureClient)captureObj;

            hr = audioClient.Start();
            ThrowIfHrFailed(hr, $"IAudioClient.Start ({sourceMode})");

            ready.TrySetResult(true);

            // 20ms silent fallback buffer when process loopback pauses event signaling during pure silence
            var idleSilenceStereo = new float[960 * 2];

            while (!token.IsCancellationRequested)
            {
                bool signaled = useEventCallback
                    ? sampleEvent!.WaitOne(25)
                    : !token.WaitHandle.WaitOne(10);
                if (token.IsCancellationRequested) break;

                bool gotAnyPacket = false;
                while (!token.IsCancellationRequested)
                {
                    hr = captureClient.GetNextPacketSize(out uint packetFrames);
                    ThrowIfHrFailed(hr, "GetNextPacketSize");
                    if (packetFrames == 0) break;

                    hr = captureClient.GetBuffer(out IntPtr dataPtr, out uint numFrames, out uint flags, out _, out _);
                    ThrowIfHrFailed(hr, "GetBuffer");

                    try
                    {
                        if (numFrames > 0)
                        {
                            gotAnyPacket = true;
                            var stereo = ConvertPacketToStereoFloat(dataPtr, (int)numFrames, targetChannels, formatTag, (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0);
                            onStereo48kFrames(stereo, isStereoSource);
                        }
                    }
                    finally
                    {
                        captureClient.ReleaseBuffer(numFrames);
                    }
                }

                if (!gotAnyPacket && !signaled && sourceMode == "process")
                {
                    onStereo48kFrames(idleSilenceStereo, isStereoSource);
                }
            }
        }
        finally
        {
            if (audioClient != null)
            {
                try { audioClient.Stop(); } catch { }
            }
            sampleEvent?.Dispose();
            if (formatPtr != IntPtr.Zero) Marshal.FreeHGlobal(formatPtr);
            if (captureClient != null && Marshal.IsComObject(captureClient)) Marshal.ReleaseComObject(captureClient);
            if (audioClient != null && Marshal.IsComObject(audioClient)) Marshal.ReleaseComObject(audioClient);
        }
    }

    private static float[] ConvertPacketToStereoFloat(
        IntPtr dataPtr,
        int frames,
        ushort channels,
        ushort formatTag,
        bool silent)
    {
        var stereo = new float[frames * 2];
        if (silent || dataPtr == IntPtr.Zero) return stereo;

        unsafe
        {
            if (formatTag == WAVE_FORMAT_IEEE_FLOAT)
            {
                float* src = (float*)dataPtr;
                if (channels == 2)
                {
                    Marshal.Copy(dataPtr, stereo, 0, stereo.Length);
                }
                else
                {
                    for (int i = 0; i < frames; i++)
                    {
                        float s = src[i * channels];
                        stereo[i * 2] = s;
                        stereo[i * 2 + 1] = s;
                    }
                }
            }
            else
            {
                short* src = (short*)dataPtr;
                const float scale = 1f / 32768f;
                for (int i = 0; i < frames; i++)
                {
                    float l = src[i * channels] * scale;
                    float r = channels >= 2 ? src[i * channels + 1] * scale : l;
                    stereo[i * 2] = l;
                    stereo[i * 2 + 1] = r;
                }
            }
        }

        return stereo;
    }

    private static IAudioClient ActivateDefaultEndpoint(int dataFlow, string? endpointId = null)
    {
        var type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)
                   ?? throw new InvalidOperationException("无法初始化 Windows 音频设备枚举器。");
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
        try
        {
            int hr = string.IsNullOrEmpty(endpointId)
                ? enumerator.GetDefaultAudioEndpoint(dataFlow, role: 0 /* eConsole */, out var device)
                : enumerator.GetDevice(endpointId, out device);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                var clientIid = IID_IAudioClient;
                hr = device.Activate(ref clientIid, (int)CLSCTX_ALL, IntPtr.Zero, out object clientObj);
                ThrowIfHrFailed(hr, "IMMDevice.Activate");
                return (IAudioClient)clientObj;
            }
            finally
            {
                if (Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            if (Marshal.IsComObject(enumerator)) Marshal.ReleaseComObject(enumerator);
        }
    }

    public static object[] ListMicrophones()
    {
        var type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!;
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(1, 1, out var devices));
            try
            {
                Marshal.ThrowExceptionForHR(devices.GetCount(out var count));
                var result = new List<object>();
                for (uint i = 0; i < Math.Min(count, 64u); i++)
                {
                    if (devices.Item(i, out var device) < 0) continue;
                    try
                    {
                        if (device.GetId(out var id) < 0 || device.OpenPropertyStore(0, out var properties) < 0) continue;
                        try
                        {
                            var key = new PropertyKey { Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 14 };
                            if (properties.GetValue(ref key, out var value) < 0) continue;
                            try { result.Add(new { id, name = value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? id : id }); }
                            finally { PropVariantClear(ref value); }
                        }
                        finally { Marshal.ReleaseComObject(properties); }
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
                return result.ToArray();
            }
            finally { Marshal.ReleaseComObject(devices); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    private static IAudioClient ActivateProcessLoopback(int pid)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new PlatformNotSupportedException("单进程音频抓包需要 Windows 10 Build 20348 或 Windows 11。");

        try
        {
            using var probe = Process.GetProcessById(pid);
            if (probe.HasExited) throw new InvalidOperationException($"进程 PID {pid} 已退出。");
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"未找到进程 PID {pid}，请刷新进程列表。");
        }

        var loopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
        {
            TargetProcessId = (uint)pid,
            ProcessLoopbackMode = PROCESS_LOOPBACK_MODE.ProcessLoopbackMode_IncludeTargetProcessTree
        };

        var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            ProcessLoopbackParams = loopbackParams
        };

        int blobSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr blobPtr = Marshal.AllocCoTaskMem(blobSize);
        IntPtr propVarPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT>());

        try
        {
            Marshal.StructureToPtr(activationParams, blobPtr, false);
            var propVar = new PROPVARIANT
            {
                vt = 65, // VT_BLOB
                cbSize = (uint)blobSize,
                pBlobData = blobPtr
            };
            Marshal.StructureToPtr(propVar, propVarPtr, false);

            var handler = new ActivateAudioCompletionHandler();
            var clientIid = IID_IAudioClient;
            ActivateAudioInterfaceAsync(
                "VAD\\Process_Loopback",
                ref clientIid,
                propVarPtr,
                handler,
                out _);

            if (!handler.Wait(5000, out int activateHr, out object? obj) || obj == null)
            {
                if (activateHr < 0) Marshal.ThrowExceptionForHR(activateHr);
                throw new TimeoutException("等待 Windows Process Loopback 初始化超时。");
            }

            if (activateHr < 0) Marshal.ThrowExceptionForHR(activateHr);
            return (IAudioClient)obj;
        }
        finally
        {
            Marshal.FreeHGlobal(propVarPtr);
            Marshal.FreeCoTaskMem(blobPtr);
        }
    }

    private static IntPtr AllocWaveFormatEx(ushort formatTag, ushort channels, int sampleRate, ushort bitsPerSample)
    {
        var wfx = new WAVEFORMATEX
        {
            wFormatTag = formatTag,
            nChannels = channels,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = bitsPerSample,
            nBlockAlign = (ushort)(channels * (bitsPerSample / 8)),
            cbSize = 0
        };
        wfx.nAvgBytesPerSec = wfx.nSamplesPerSec * wfx.nBlockAlign;
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
        Marshal.StructureToPtr(wfx, ptr, false);
        return ptr;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
    }

    private static void ThrowIfHrFailed(int hr, string stage)
    {
        if (hr >= 0) return;
        var ex = Marshal.GetExceptionForHR(hr);
        throw new COMException($"WASAPI [{stage}] 失败: {ex?.Message ?? $"HRESULT 0x{hr:X8}"}", hr);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [In, MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        [In] IntPtr activationParams,
        [In] IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    private enum PROCESS_LOOPBACK_MODE
    {
        ProcessLoopbackMode_IncludeTargetProcessTree = 0,
        ProcessLoopbackMode_ExcludeTargetProcessTree = 1
    }

    private enum AUDIOCLIENT_ACTIVATION_TYPE
    {
        AUDIOCLIENT_ACTIVATION_TYPE_DEFAULT = 0,
        AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA588")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted([In] IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(
            out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            [In] ref Guid iid,
            int dwClsCtx,
            IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out int pdwState);
    }

    [ComImport, Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyValue value);

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(
            int shareMode,
            uint streamFlags,
            long hnsBufferDuration,
            long hnsPeriodicity,
            IntPtr pFormat,
            IntPtr audioSessionGuid);

        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService(
            [In] ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(
            out IntPtr ppData,
            out uint pNumFramesToRead,
            out uint pdwFlags,
            out ulong pu64DevicePosition,
            out ulong pu64QPCPosition);

        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    private sealed class ActivateAudioCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly ManualResetEventSlim done = new(false);
        private int hr = unchecked((int)0x80004005);
        private object? result;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                int callHr = activateOperation.GetActivateResult(out int actHr, out object activated);
                hr = callHr < 0 ? callHr : actHr;
                result = hr >= 0 ? activated : null;
            }
            catch (Exception ex)
            {
                hr = ex.HResult;
                result = null;
            }
            finally
            {
                done.Set();
            }
        }

        public bool Wait(int timeoutMs, out int activateHr, out object? activatedObj)
        {
            bool signaled = done.Wait(timeoutMs);
            activateHr = hr;
            activatedObj = result;
            return signaled;
        }
    }
}
