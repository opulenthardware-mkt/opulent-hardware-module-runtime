using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpulentHardware.VolumeMixer;

internal sealed record MixerSnapshot(
    string ProtocolVersion,
    DateTimeOffset Timestamp,
    AudioChannel Output,
    string OutputDeviceId,
    IReadOnlyList<OutputDevice> Outputs,
    IReadOnlyList<AudioChannel> Sessions,
    MicrophoneSnapshot Microphone);

internal sealed record OutputDevice(string Id, string Name, bool IsDefault);

internal sealed record MicrophoneSnapshot(
    bool Available,
    string? DeviceId,
    string Name,
    IReadOnlyList<OutputDevice> Devices,
    float Volume,
    bool Muted,
    float Peak,
    IReadOnlyList<InputApplication> ActiveApplications);

internal sealed record InputApplication(string Name, string? ProcessName, uint? ProcessId, string? IconDataUrl);

internal sealed record AudioChannel(
    string Id,
    string Kind,
    string Name,
    string? ProcessName,
    uint? ProcessId,
    float Volume,
    bool Muted,
    float Peak,
    bool Active,
    string? IconDataUrl);

internal static class AudioMixer
{
    private const uint ClsctxAll = 23;
    private const uint CoinitMultithreaded = 0x0;
    private static readonly Guid EventContext = new("640C078B-ED73-49A0-9F9B-6A2C0EDFC26A");
    private static readonly Dictionary<string, string?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    internal static string? LastError { get; private set; }

    internal static bool TryReadSnapshot(out MixerSnapshot? snapshot)
    {
        lock (Gate)
        {
            int init = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
            bool uninitialize = init >= 0;
            try
            {
                LastError = null;
                using var graph = AudioGraph.Open();
                AudioChannel output = graph.ReadMaster();
                IReadOnlyList<OutputDevice> outputs = graph.ReadOutputs(out string outputDeviceId);
                List<AudioChannel> sessions = graph.ReadSessions();
                MicrophoneSnapshot microphone;
                try
                {
                    using var input = InputGraph.Open();
                    microphone = input.ReadSnapshot();
                }
                catch
                {
                    microphone = new MicrophoneSnapshot(false, null, "NO MICROPHONE DETECTED", Array.Empty<OutputDevice>(), 0, false, 0, Array.Empty<InputApplication>());
                }
                snapshot = new MixerSnapshot(Program.ProtocolVersion, DateTimeOffset.UtcNow, output, outputDeviceId, outputs, sessions, microphone);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                snapshot = null;
                return false;
            }
            finally
            {
                if (uninitialize) CoUninitialize();
            }
        }
    }

    internal static bool TryUpdateChannel(string channelId, ChannelUpdate update, out string? error)
    {
        lock (Gate)
        {
            int init = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
            bool uninitialize = init >= 0;
            try
            {
                using var graph = AudioGraph.Open();
                bool updated = channelId.Equals("master", StringComparison.OrdinalIgnoreCase)
                    ? graph.UpdateMaster(update)
                    : graph.UpdateSession(channelId, update);
                error = updated ? null : "The requested channel may have closed or changed since the last refresh.";
                return updated;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (uninitialize) CoUninitialize();
            }
        }
    }

    internal static bool TrySetDefaultOutput(string deviceId, out string? error)
    {
        lock (Gate)
        {
            int init = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
            bool uninitialize = init >= 0;
            try
            {
                using var graph = AudioGraph.Open();
                if (!graph.ReadOutputs(out _).Any(device => device.Id.Equals(deviceId, StringComparison.Ordinal)))
                {
                    error = "The requested playback device is not active.";
                    return false;
                }
                var policy = (IPolicyConfig)(object)new PolicyConfigClientComObject();
                try
                {
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Console));
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Multimedia));
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Communications));
                }
                finally
                {
                    if (Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy);
                }
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (uninitialize) CoUninitialize();
            }
        }
    }

    internal static bool TryUpdateMicrophone(ChannelUpdate update, out string? error)
    {
        lock (Gate)
        {
            int init = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
            bool uninitialize = init >= 0;
            try
            {
                using var graph = InputGraph.Open();
                graph.Update(update);
                error = null;
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { if (uninitialize) CoUninitialize(); }
        }
    }

    internal static bool TrySetDefaultInput(string deviceId, out string? error)
    {
        lock (Gate)
        {
            int init = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
            bool uninitialize = init >= 0;
            try
            {
                using var graph = InputGraph.Open();
                if (!graph.ReadDevices(out _).Any(device => device.Id.Equals(deviceId, StringComparison.Ordinal)))
                {
                    error = "The requested microphone is not active.";
                    return false;
                }
                SetDefaultEndpoint(deviceId);
                error = null;
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { if (uninitialize) CoUninitialize(); }
        }
    }

    private static void SetDefaultEndpoint(string deviceId)
    {
        var policy = (IPolicyConfig)(object)new PolicyConfigClientComObject();
        try
        {
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Console));
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Multimedia));
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Communications));
        }
        finally { if (Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy); }
    }

    private static string SessionId(string instanceIdentifier)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(instanceIdentifier));
        return Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant();
    }

    private static string ApplicationKey(uint processId, string displayName)
    {
        if (processId == 0) return "system-sounds";
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName.Trim().ToLowerInvariant();
        }
        catch
        {
            string fallback = string.IsNullOrWhiteSpace(displayName) ? "application-" + processId : displayName;
            return fallback.Trim().ToLowerInvariant();
        }
    }

    private static string FriendlyName(uint processId, string displayName, out string? processName, out string? icon)
    {
        processName = null;
        icon = null;
        if (processId == 0) return string.IsNullOrWhiteSpace(displayName) ? "System Sounds" : displayName;
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            string? executable = null;
            string? description = null;
            try
            {
                executable = process.MainModule?.FileName;
                description = process.MainModule?.FileVersionInfo.FileDescription;
                if (string.IsNullOrWhiteSpace(description)) description = process.MainModule?.FileVersionInfo.ProductName;
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(executable))
            {
                lock (IconCache)
                {
                    if (!IconCache.TryGetValue(executable, out icon))
                    {
                        icon = ExtractIcon(executable);
                        if (IconCache.Count > 128) IconCache.Clear();
                        IconCache[executable] = icon;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith('@')) return displayName;
            if (!string.IsNullOrWhiteSpace(description)) return description;
            return process.ProcessName;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith('@')) return displayName;
            return "Application";
        }
    }

    private static string? ExtractIcon(string executable)
    {
        try
        {
            using Icon? icon = Icon.ExtractAssociatedIcon(executable);
            if (icon is null) return null;
            using Bitmap bitmap = icon.ToBitmap();
            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(memory.ToArray());
        }
        catch { return null; }
    }

    private static string ReadDeviceName(IMMDevice device, string fallback)
    {
        IPropertyStore? store = null;
        PropVariant value = default;
        try
        {
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out store));
            PropertyKey key = PropertyKey.DeviceFriendlyName;
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out value));
            return value.GetString() ?? fallback;
        }
        catch { return fallback; }
        finally
        {
            PropVariantClear(ref value);
            ReleaseCom(store);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); } catch { }
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant propVariant);

    private sealed class AudioGraph : IDisposable
    {
        private readonly IMMDeviceEnumerator _enumerator;
        private readonly IMMDevice _device;
        private readonly IAudioEndpointVolume _endpoint;
        private readonly IAudioMeterInformation _endpointMeter;
        private readonly IAudioSessionManager2 _sessions;

        private AudioGraph(IMMDeviceEnumerator enumerator, IMMDevice device, IAudioEndpointVolume endpoint, IAudioMeterInformation endpointMeter, IAudioSessionManager2 sessions)
        {
            _enumerator = enumerator;
            _device = device;
            _endpoint = endpoint;
            _endpointMeter = endpointMeter;
            _sessions = sessions;
        }

        internal static AudioGraph Open()
        {
            var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out IMMDevice device));
            Guid endpointIid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref endpointIid, ClsctxAll, IntPtr.Zero, out object endpointObject));
            Guid meterIid = typeof(IAudioMeterInformation).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref meterIid, ClsctxAll, IntPtr.Zero, out object meterObject));
            Guid sessionsIid = typeof(IAudioSessionManager2).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref sessionsIid, ClsctxAll, IntPtr.Zero, out object sessionsObject));
            return new AudioGraph(enumerator, device, (IAudioEndpointVolume)endpointObject, (IAudioMeterInformation)meterObject, (IAudioSessionManager2)sessionsObject);
        }

        internal AudioChannel ReadMaster()
        {
            Marshal.ThrowExceptionForHR(_endpoint.GetMasterVolumeLevelScalar(out float volume));
            Marshal.ThrowExceptionForHR(_endpoint.GetMute(out bool muted));
            float peak = 0;
            _endpointMeter.GetPeakValue(out peak);
            return new AudioChannel("master", "output", "SYSTEM OUTPUT", null, null, volume, muted, peak, true, null);
        }

        internal IReadOnlyList<OutputDevice> ReadOutputs(out string currentDeviceId)
        {
            Marshal.ThrowExceptionForHR(_device.GetId(out currentDeviceId));
            const uint DeviceStateActive = 0x1;
            Marshal.ThrowExceptionForHR(_enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceStateActive, out IntPtr collectionPointer));
            IMMDeviceCollection collection;
            try { collection = (IMMDeviceCollection)Marshal.GetTypedObjectForIUnknown(collectionPointer, typeof(IMMDeviceCollection)); }
            finally { if (collectionPointer != IntPtr.Zero) Marshal.Release(collectionPointer); }
            var outputs = new List<OutputDevice>();
            try
            {
                Marshal.ThrowExceptionForHR(collection.GetCount(out uint count));
                for (uint index = 0; index < count; index++)
                {
                    IMMDevice? device = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(collection.Item(index, out device));
                        Marshal.ThrowExceptionForHR(device.GetId(out string id));
                        outputs.Add(new OutputDevice(id, AudioMixer.ReadDeviceName(device, "Audio Output"), id.Equals(currentDeviceId, StringComparison.Ordinal)));
                    }
                    catch { }
                    finally { Release(device); }
                }
            }
            finally { Release(collection); }
            return outputs.OrderByDescending(device => device.IsDefault).ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal bool UpdateMaster(ChannelUpdate update)
        {
            if (update.Volume is float volume)
            {
                Guid context = EventContext;
                Marshal.ThrowExceptionForHR(_endpoint.SetMasterVolumeLevelScalar(volume, ref context));
            }
            if (update.Muted is bool muted)
            {
                Guid context = EventContext;
                Marshal.ThrowExceptionForHR(_endpoint.SetMute(muted, ref context));
            }
            return true;
        }

        internal List<AudioChannel> ReadSessions()
        {
            var samples = new List<(string Key, AudioChannel Channel)>();
            Marshal.ThrowExceptionForHR(_sessions.GetSessionEnumerator(out IAudioSessionEnumerator enumerator));
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.GetCount(out int count));
                for (int index = 0; index < count; index++)
                {
                    IAudioSessionControl? control = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(enumerator.GetSession(index, out control));
                        if (control is not IAudioSessionControl2 control2 || control is not ISimpleAudioVolume volumeControl) continue;
                        control.GetState(out AudioSessionState state);
                        if (state == AudioSessionState.Expired) continue;
                        control.GetDisplayName(out string displayName);
                        control2.GetProcessId(out uint processId);
                        control2.GetSessionInstanceIdentifier(out string instanceIdentifier);
                        volumeControl.GetMasterVolume(out float volume);
                        volumeControl.GetMute(out bool muted);
                        float peak = 0;
                        if (control is IAudioMeterInformation meter) meter.GetPeakValue(out peak);
                        string name = FriendlyName(processId, displayName, out string? processName, out string? icon);
                        string applicationKey = ApplicationKey(processId, displayName);
                        samples.Add((applicationKey, new AudioChannel(SessionId(instanceIdentifier), "session", name.ToUpperInvariant(), processName, processId, volume, muted, peak, state == AudioSessionState.Active, icon)));
                    }
                    catch { }
                    finally { Release(control); }
                }
            }
            finally { Release(enumerator); }
            return samples
                .GroupBy(sample => sample.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    List<AudioChannel> channels = group.Select(sample => sample.Channel).ToList();
                    AudioChannel representative = channels.OrderByDescending(channel => channel.Active).ThenBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase).First();
                    return new AudioChannel(
                        SessionId("application:" + group.Key.ToLowerInvariant()),
                        "application",
                        representative.Name,
                        representative.ProcessName,
                        representative.ProcessId,
                        channels.Average(channel => channel.Volume),
                        channels.All(channel => channel.Muted),
                        channels.Max(channel => channel.Peak),
                        channels.Any(channel => channel.Active),
                        channels.Select(channel => channel.IconDataUrl).FirstOrDefault(icon => !string.IsNullOrWhiteSpace(icon)));
                })
                .OrderBy(channel => channel.ProcessId == 0 ? 0 : 1)
                .ThenBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(channel => channel.Id, StringComparer.Ordinal)
                .ToList();
        }

        internal bool UpdateSession(string requestedId, ChannelUpdate update)
        {
            Marshal.ThrowExceptionForHR(_sessions.GetSessionEnumerator(out IAudioSessionEnumerator enumerator));
            bool updatedAny = false;
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.GetCount(out int count));
                for (int index = 0; index < count; index++)
                {
                    IAudioSessionControl? control = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(enumerator.GetSession(index, out control));
                        if (control is not IAudioSessionControl2 control2 || control is not ISimpleAudioVolume volumeControl) continue;
                        control2.GetSessionInstanceIdentifier(out string instanceIdentifier);
                        control.GetDisplayName(out string displayName);
                        control2.GetProcessId(out uint processId);
                        string applicationId = SessionId("application:" + ApplicationKey(processId, displayName));
                        bool isRequested = applicationId.Equals(requestedId, StringComparison.OrdinalIgnoreCase) || SessionId(instanceIdentifier).Equals(requestedId, StringComparison.OrdinalIgnoreCase);
                        if (!isRequested) continue;
                        if (update.Volume is float volume)
                        {
                            Guid context = EventContext;
                            Marshal.ThrowExceptionForHR(volumeControl.SetMasterVolume(volume, ref context));
                        }
                        if (update.Muted is bool muted)
                        {
                            Guid context = EventContext;
                            Marshal.ThrowExceptionForHR(volumeControl.SetMute(muted, ref context));
                        }
                        updatedAny = true;
                    }
                    finally { Release(control); }
                }
                return updatedAny;
            }
            finally { Release(enumerator); }
        }

        public void Dispose()
        {
            Release(_sessions);
            Release(_endpointMeter);
            Release(_endpoint);
            Release(_device);
            Release(_enumerator);
        }

        private static void Release(object? value)
        {
            if (value is not null && Marshal.IsComObject(value))
            {
                try { Marshal.FinalReleaseComObject(value); } catch { }
            }
        }
    }

    private sealed class InputGraph : IDisposable
    {
        private readonly IMMDeviceEnumerator _enumerator;
        private readonly IMMDevice _device;
        private readonly IAudioEndpointVolume _endpoint;
        private readonly IAudioMeterInformation _meter;
        private readonly IAudioSessionManager2 _sessions;

        private InputGraph(IMMDeviceEnumerator enumerator, IMMDevice device, IAudioEndpointVolume endpoint, IAudioMeterInformation meter, IAudioSessionManager2 sessions)
        {
            _enumerator = enumerator; _device = device; _endpoint = endpoint; _meter = meter; _sessions = sessions;
        }

        internal static InputGraph Open()
        {
            var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Multimedia, out IMMDevice device));
            Guid endpointIid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref endpointIid, ClsctxAll, IntPtr.Zero, out object endpointObject));
            Guid meterIid = typeof(IAudioMeterInformation).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref meterIid, ClsctxAll, IntPtr.Zero, out object meterObject));
            Guid sessionsIid = typeof(IAudioSessionManager2).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref sessionsIid, ClsctxAll, IntPtr.Zero, out object sessionsObject));
            return new InputGraph(enumerator, device, (IAudioEndpointVolume)endpointObject, (IAudioMeterInformation)meterObject, (IAudioSessionManager2)sessionsObject);
        }

        internal MicrophoneSnapshot ReadSnapshot()
        {
            IReadOnlyList<OutputDevice> devices = ReadDevices(out string currentId);
            Marshal.ThrowExceptionForHR(_endpoint.GetMasterVolumeLevelScalar(out float volume));
            Marshal.ThrowExceptionForHR(_endpoint.GetMute(out bool muted));
            float peak = 0; _meter.GetPeakValue(out peak);
            string name = devices.FirstOrDefault(device => device.IsDefault)?.Name ?? AudioMixer.ReadDeviceName(_device, "Microphone");
            return new MicrophoneSnapshot(true, currentId, name, devices, volume, muted, peak, ReadActiveApplications());
        }

        internal IReadOnlyList<OutputDevice> ReadDevices(out string currentDeviceId)
        {
            Marshal.ThrowExceptionForHR(_device.GetId(out currentDeviceId));
            Marshal.ThrowExceptionForHR(_enumerator.EnumAudioEndpoints(EDataFlow.Capture, 0x1, out IntPtr pointer));
            IMMDeviceCollection collection;
            try { collection = (IMMDeviceCollection)Marshal.GetTypedObjectForIUnknown(pointer, typeof(IMMDeviceCollection)); }
            finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
            var devices = new List<OutputDevice>();
            try
            {
                Marshal.ThrowExceptionForHR(collection.GetCount(out uint count));
                for (uint index = 0; index < count; index++)
                {
                    IMMDevice? device = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(collection.Item(index, out device));
                        Marshal.ThrowExceptionForHR(device.GetId(out string id));
                        devices.Add(new OutputDevice(id, AudioMixer.ReadDeviceName(device, "Microphone"), id.Equals(currentDeviceId, StringComparison.Ordinal)));
                    }
                    catch { }
                    finally { ReleaseCom(device); }
                }
            }
            finally { ReleaseCom(collection); }
            return devices.OrderByDescending(device => device.IsDefault).ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private IReadOnlyList<InputApplication> ReadActiveApplications()
        {
            var apps = new List<(string Key, InputApplication App)>();
            Marshal.ThrowExceptionForHR(_sessions.GetSessionEnumerator(out IAudioSessionEnumerator enumerator));
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.GetCount(out int count));
                for (int index = 0; index < count; index++)
                {
                    IAudioSessionControl? control = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(enumerator.GetSession(index, out control));
                        if (control is not IAudioSessionControl2 control2) continue;
                        control.GetState(out AudioSessionState state);
                        if (state != AudioSessionState.Active) continue;
                        control.GetDisplayName(out string displayName);
                        control2.GetProcessId(out uint processId);
                        string name = FriendlyName(processId, displayName, out string? processName, out string? icon);
                        apps.Add((ApplicationKey(processId, displayName), new InputApplication(name, processName, processId, icon)));
                    }
                    catch { }
                    finally { ReleaseCom(control); }
                }
            }
            finally { ReleaseCom(enumerator); }
            return apps.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First().App)
                .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal void Update(ChannelUpdate update)
        {
            if (update.Volume is float volume) { Guid context = EventContext; Marshal.ThrowExceptionForHR(_endpoint.SetMasterVolumeLevelScalar(volume, ref context)); }
            if (update.Muted is bool muted) { Guid context = EventContext; Marshal.ThrowExceptionForHR(_endpoint.SetMute(muted, ref context)); }
        }

        public void Dispose()
        {
            ReleaseCom(_sessions); ReleaseCom(_meter); ReleaseCom(_endpoint); ReleaseCom(_device); ReleaseCom(_enumerator);
        }
    }
}

internal enum EDataFlow { Render, Capture, All }
internal enum ERole { Console, Multimedia, Communications }
internal enum AudioSessionState { Inactive, Active, Expired }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class MMDeviceEnumeratorComObject { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsctx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint propertyCount);
    [PreserveSig] int GetAt(uint propertyIndex, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    internal Guid FormatId;
    internal uint PropertyId;
    internal static PropertyKey DeviceFriendlyName => new() { FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 14 };
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)] internal ushort VariantType;
    [FieldOffset(8)] internal IntPtr PointerValue;

    internal readonly string? GetString() => VariantType switch
    {
        31 => Marshal.PtrToStringUni(PointerValue),
        8 => Marshal.PtrToStringBSTR(PointerValue),
        _ => null
    };
}

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal sealed class PolicyConfigClientComObject { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, out IntPtr format);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, out long period, out long minimumPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out uint channelCount);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
internal interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
    [PreserveSig] int GetMeteringChannelCount(out int channelCount);
    [PreserveSig] int GetChannelsPeakValues(int channelCount, IntPtr peakValues);
    [PreserveSig] int QueryHardwareSupport(out int hardwareSupportMask);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
internal interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(ref Guid audioSessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
    [PreserveSig] int GetSimpleAudioVolume(ref Guid audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
    [PreserveSig] int RegisterSessionNotification(IntPtr sessionNotification);
    [PreserveSig] int UnregisterSessionNotification(IntPtr sessionNotification);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
    [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
internal interface IAudioSessionControl
{
    [PreserveSig] int GetState(out AudioSessionState state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingId);
    [PreserveSig] int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl methods must be repeated here. COM interface inheritance is
    // not flattened reliably by .NET interop; omitting these entries shifts the vtable
    // and makes every GetProcessId call read as process 0 ("System Sounds").
    [PreserveSig] int GetState(out AudioSessionState state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingId);
    [PreserveSig] int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionIdentifier);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionInstanceIdentifier);
    [PreserveSig] int GetProcessId(out uint processId);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
internal interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
