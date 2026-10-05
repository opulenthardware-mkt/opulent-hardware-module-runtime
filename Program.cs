using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;

namespace OpulentHardware.VolumeMixer;

internal static class Program
{
    internal const int Port = 17652;
    internal const string RuntimeId = "com.opulenthardware.module-runtime";
    internal const string RuntimeName = "Opulent Hardware Module Runtime";
    internal const string Version = "1.0.0";
    internal const string ProtocolVersion = "1.0";
    internal static readonly string[] Capabilities =
    [
        "runtime.health.v1",
        "runtime.handshake.v1",
        "runtime.diagnostics.v1",
        "audio.master.v1",
        "audio.sessions.v1",
        "audio.output-devices.v1",
        "audio.input-devices.v1",
        "audio.microphone.v1",
        "media.now-playing.v1",
        "media.artwork.v1",
        "media.transport.v1",
        "media.seek.v1"
    ];
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--media-json", StringComparer.OrdinalIgnoreCase))
        {
            MediaSnapshot media = MediaModule.ReadAsync().GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(media, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            Environment.ExitCode = 0;
            return;
        }
        if (args.Contains("--diagnostics-json", StringComparer.OrdinalIgnoreCase))
        {
            if (AudioMixer.TryReadSnapshot(out MixerSnapshot? diagnosticSnapshot) && diagnosticSnapshot is not null)
            {
                Console.WriteLine(JsonSerializer.Serialize(diagnosticSnapshot, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                Environment.ExitCode = 0;
            }
            else
            {
                Console.Error.WriteLine(AudioMixer.LastError ?? "Audio snapshot failed.");
                Environment.ExitCode = 1;
            }
            return;
        }
        bool selfTest = args.Contains("--self-test", StringComparer.OrdinalIgnoreCase);
        if (selfTest)
        {
            bool originsPass = TrayApplicationContext.IsAllowedLocalOrigin("null") &&
                TrayApplicationContext.IsAllowedLocalOrigin("file://") &&
                TrayApplicationContext.IsAllowedLocalOrigin("icue://widget") &&
                TrayApplicationContext.IsAllowedLocalOrigin("http://127.0.0.1:17652") &&
                !TrayApplicationContext.IsAllowedLocalOrigin("https://example.com");
            bool audioPass = AudioMixer.TryReadSnapshot(out MixerSnapshot? snapshot) && snapshot is not null;
            bool sessionMappingPass = !audioPass || snapshot!.Sessions.Count == 0 || snapshot.Sessions.Any(session => session.ProcessId is > 0);
            bool capabilitiesPass = Capabilities.Contains("runtime.handshake.v1") && Capabilities.Contains("audio.sessions.v1");
            if (!audioPass) Console.Error.WriteLine(AudioMixer.LastError ?? "Audio snapshot failed.");
            else if (!sessionMappingPass) Console.Error.WriteLine("Audio sessions were returned, but none contained a process ID.");
            Environment.ExitCode = audioPass && sessionMappingPass && originsPass && capabilitiesPass ? 0 : 1;
            return;
        }

        _singleInstance = new Mutex(true, @"Local\OpulentHardware.VolumeMixer.Companion", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Opulent Hardware Module Runtime is already running in the notification area.",
                Program.RuntimeName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
        _singleInstance.ReleaseMutex();
        _singleInstance.Dispose();
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Icon _brandIcon;
    private WebApplication? _webApp;
    private StatusForm? _statusForm;

    internal TrayApplicationContext()
    {
        _brandIcon = BrandIcon.Create();
        _statusItem = new ToolStripMenuItem("Starting local module runtime…") { Enabled = false };
        StartupRegistration.EnsureDefaultEnabled();
        StartupRegistration.EnsureCurrentExecutable();
        _startupItem = new ToolStripMenuItem("Start with Windows") { Checked = StartupRegistration.IsEnabled(), CheckOnClick = true };
        _startupItem.CheckedChanged += (_, _) => StartupRegistration.SetEnabled(_startupItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Status", null, (_, _) => ShowStatus());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());

        _trayIcon = new NotifyIcon
        {
            Icon = _brandIcon,
            Text = Program.RuntimeName,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowStatus();

        _ = StartServerAsync();
    }

    private async Task StartServerAsync()
    {
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ApplicationName = typeof(Program).Assembly.FullName
            });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, Program.Port));
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            });

            _webApp = builder.Build();
            _webApp.Use(async (context, next) =>
            {
                string origin = context.Request.Headers.Origin.ToString();
                bool allowedOrigin = IsAllowedLocalOrigin(origin);
                if (!allowedOrigin)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new ApiError("origin_denied", "This local service accepts only iCUE/file previews and local development origins."));
                    return;
                }

                if (!string.IsNullOrEmpty(origin))
                {
                    context.Response.Headers.AccessControlAllowOrigin = origin;
                    context.Response.Headers.Vary = "Origin";
                }
                context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
                context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
                context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";

                if (HttpMethods.IsOptions(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }
                await next();
            });

            _webApp.MapGet("/api/v1/health", () => Results.Ok(new
            {
                service = Program.RuntimeName,
                runtimeId = Program.RuntimeId,
                version = Program.Version,
                protocolVersion = Program.ProtocolVersion,
                platform = "windows",
                port = Program.Port
            }));

            _webApp.MapGet("/api/v1/runtime", () => Results.Ok(RuntimeDescriptor()));

            _webApp.MapGet("/api/v1/capabilities", () => Results.Ok(new
            {
                runtimeId = Program.RuntimeId,
                runtimeVersion = Program.Version,
                protocolVersion = Program.ProtocolVersion,
                capabilities = Program.Capabilities
            }));

            _webApp.MapPost("/api/v1/handshake", async (HttpRequest request) =>
            {
                RuntimeHandshake? handshake;
                try { handshake = await request.ReadFromJsonAsync<RuntimeHandshake>(); }
                catch (JsonException) { return Results.BadRequest(new ApiError("invalid_json", "Expected a JSON runtime handshake.")); }
                if (handshake is null || string.IsNullOrWhiteSpace(handshake.ClientId))
                    return Results.BadRequest(new ApiError("missing_client", "Specify a stable clientId."));

                string[] requested = handshake.RequiredCapabilities ?? [];
                string[] missing = requested.Except(Program.Capabilities, StringComparer.OrdinalIgnoreCase).ToArray();
                return Results.Ok(new
                {
                    accepted = missing.Length == 0,
                    clientId = handshake.ClientId,
                    clientVersion = handshake.ClientVersion,
                    runtime = RuntimeDescriptor(),
                    capabilities = Program.Capabilities,
                    missingCapabilities = missing
                });
            });

            _webApp.MapGet("/api/v1/diagnostics", () =>
            {
                bool audioReady = AudioMixer.TryReadSnapshot(out MixerSnapshot? snapshot) && snapshot is not null;
                return Results.Ok(new
                {
                    runtime = RuntimeDescriptor(),
                    audioReady,
                    sessionCount = snapshot?.Sessions.Count ?? 0,
                    playbackDeviceCount = snapshot?.Outputs.Count ?? 0,
                    microphoneAvailable = snapshot?.Microphone.Available ?? false,
                    microphoneDeviceCount = snapshot?.Microphone.Devices.Count ?? 0,
                    lastAudioError = audioReady ? null : AudioMixer.LastError
                });
            });

            _webApp.MapGet("/api/v1/media", async () => Results.Ok(await MediaModule.ReadAsync()));

            _webApp.MapGet("/api/v1/media/artwork/{artworkId}", (string artworkId) =>
            {
                if (!MediaModule.TryGetArtwork(artworkId, out byte[]? bytes, out string mime) || bytes is null)
                    return Results.NotFound(new ApiError("artwork_unavailable", "The requested media artwork is no longer current."));
                return Results.File(bytes, mime);
            });

            _webApp.MapPost("/api/v1/media/command", async (HttpRequest request) =>
            {
                MediaCommand? command;
                try { command = await request.ReadFromJsonAsync<MediaCommand>(); }
                catch (JsonException) { return Results.BadRequest(new ApiError("invalid_json", "Expected a JSON media command.")); }
                if (command is null || string.IsNullOrWhiteSpace(command.Command))
                    return Results.BadRequest(new ApiError("missing_command", "Specify previous, playPause, next, or seekRelative."));
                bool succeeded;
                if (command.Command.Equals("seekRelative", StringComparison.OrdinalIgnoreCase))
                {
                    if (command.OffsetMs is null || command.OffsetMs == 0)
                        return Results.BadRequest(new ApiError("missing_offset", "seekRelative requires a non-zero offsetMs value."));
                    succeeded = await MediaModule.SeekRelativeAsync(command.OffsetMs.Value);
                }
                else
                {
                    succeeded = await MediaModule.CommandAsync(command.Command);
                }
                if (!succeeded)
                    return Results.Json(new ApiError("media_unavailable", "The media session does not support that command."), statusCode: 409);
                return Results.Ok(new { ok = true, command = command.Command, offsetMs = command.OffsetMs });
            });

            _webApp.MapGet("/api/v1/mixer", () =>
            {
                if (!AudioMixer.TryReadSnapshot(out MixerSnapshot? snapshot) || snapshot is null)
                    return Results.Json(new ApiError("audio_unavailable", "Windows audio sessions are temporarily unavailable."), statusCode: 503);
                return Results.Ok(snapshot);
            });

            _webApp.MapPost("/api/v1/channels/{channelId}", async (string channelId, HttpRequest request) =>
            {
                ChannelUpdate? update;
                try { update = await request.ReadFromJsonAsync<ChannelUpdate>(); }
                catch (JsonException) { return Results.BadRequest(new ApiError("invalid_json", "Expected a JSON channel update.")); }
                if (update is null || (update.Volume is null && update.Muted is null))
                    return Results.BadRequest(new ApiError("empty_update", "Specify volume and/or muted."));
                if (update.Volume is < 0 or > 1)
                    return Results.BadRequest(new ApiError("invalid_volume", "Volume must be between 0 and 1."));
                if (!AudioMixer.TryUpdateChannel(channelId, update, out string? error))
                    return Results.NotFound(new ApiError("channel_unavailable", error ?? "The audio channel is no longer available."));
                return Results.Ok(new { ok = true, channelId });
            });

            _webApp.MapPost("/api/v1/output", async (HttpRequest request) =>
            {
                OutputSelection? selection;
                try { selection = await request.ReadFromJsonAsync<OutputSelection>(); }
                catch (JsonException) { return Results.BadRequest(new ApiError("invalid_json", "Expected a JSON output selection.")); }
                if (selection is null || string.IsNullOrWhiteSpace(selection.DeviceId))
                    return Results.BadRequest(new ApiError("missing_device", "Specify a playback device ID."));
                if (!AudioMixer.TrySetDefaultOutput(selection.DeviceId, out string? error))
                    return Results.NotFound(new ApiError("output_unavailable", error ?? "The playback device is unavailable."));
                return Results.Ok(new { ok = true, deviceId = selection.DeviceId });
            });

            _webApp.MapPost("/api/v1/input", async (HttpRequest request) =>
            {
                ChannelUpdate? update;
                try { update = await request.ReadFromJsonAsync<ChannelUpdate>(); }
                catch (JsonException) { return Results.BadRequest(new ApiError("invalid_json", "Expected a JSON microphone update.")); }
                if (update is null || (update.Volume is null && update.Muted is null))
                    return Results.BadRequest(new ApiError("empty_update", "Specify volume and/or muted."));
                if (update.Volume is < 0 or > 1)
                    return Results.BadRequest(new ApiError("invalid_volume", "Volume must be between 0 and 1."));
                if (!AudioMixer.TryUpdateMicrophone(update, out string? error))
                    return Results.NotFound(new ApiError("input_unavailable", error ?? "The microphone is unavailable."));
                return Results.Ok(new { ok = true });
            });

            _webApp.MapPost("/api/v1/input/device", async (HttpRequest request) =>
            {
                OutputSelection? selection;
                try { selection = await request.ReadFromJsonAsync<OutputSelection>(); }
                catch (JsonException) { return Results.BadRequest(new ApiError("invalid_json", "Expected a JSON microphone selection.")); }
                if (selection is null || string.IsNullOrWhiteSpace(selection.DeviceId))
                    return Results.BadRequest(new ApiError("missing_device", "Specify a microphone device ID."));
                if (!AudioMixer.TrySetDefaultInput(selection.DeviceId, out string? error))
                    return Results.NotFound(new ApiError("input_unavailable", error ?? "The microphone is unavailable."));
                return Results.Ok(new { ok = true, deviceId = selection.DeviceId });
            });

            await _webApp.StartAsync(_shutdown.Token);
            SetStatus("Runtime online — local API v1", true);
        }
        catch (Exception ex)
        {
            SetStatus("Service offline — port unavailable", false);
            _trayIcon.ShowBalloonTip(5000, Program.RuntimeName, "The local module runtime could not start. " + ex.Message, ToolTipIcon.Error);
        }
    }

    private static object RuntimeDescriptor() => new
    {
        runtimeId = Program.RuntimeId,
        name = Program.RuntimeName,
        runtimeVersion = Program.Version,
        apiVersion = 1,
        protocolVersion = Program.ProtocolVersion,
        platform = "windows",
        port = Program.Port
    };

    internal static bool IsAllowedLocalOrigin(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || origin.Equals("null", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return true;
        return uri.IsLoopback;
    }

    private void SetStatus(string text, bool online)
    {
        if (_statusItem.Owner?.InvokeRequired == true)
        {
            _statusItem.Owner.BeginInvoke(() => SetStatus(text, online));
            return;
        }
        _statusItem.Text = text;
        _trayIcon.Text = online ? "Opulent Module Runtime — online" : "Opulent Module Runtime — offline";
        _statusForm?.SetServiceStatus(text, online);
    }

    private void ShowStatus()
    {
        if (_statusForm is null || _statusForm.IsDisposed)
        {
            _statusForm = new StatusForm(_brandIcon, _statusItem.Text ?? "Starting local module runtime…");
            _statusForm.FormClosed += (_, _) => _statusForm = null;
        }
        _statusForm.Show();
        _statusForm.Activate();
    }

    private async Task ExitAsync()
    {
        _trayIcon.Visible = false;
        _shutdown.Cancel();
        if (_webApp is not null)
        {
            try { await _webApp.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
            await _webApp.DisposeAsync();
        }
        _statusForm?.Close();
        _trayIcon.Dispose();
        _brandIcon.Dispose();
        _shutdown.Dispose();
        ExitThread();
    }
}

internal sealed class StatusForm : Form
{
    private readonly Label _status;

    internal StatusForm(Icon icon, string initialStatus)
    {
        Text = Program.RuntimeName;
        Icon = icon;
        ClientSize = new Size(510, 260);
        MinimumSize = new Size(526, 299);
        BackColor = Color.FromArgb(8, 11, 11);
        ForeColor = Color.FromArgb(237, 247, 206);
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterScreen;

        var title = new Label
        {
            Text = "OPULENT HARDWARE",
            ForeColor = Color.FromArgb(200, 255, 25),
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            Location = new Point(30, 30),
            AutoSize = true
        };
        var subtitle = new Label
        {
            Text = "MODULE RUNTIME  •  LOCAL CAPABILITY BUS",
            Location = new Point(33, 75),
            AutoSize = true
        };
        _status = new Label
        {
            Text = initialStatus,
            ForeColor = Color.FromArgb(200, 255, 25),
            Location = new Point(33, 124),
            AutoSize = true,
            Font = new Font("Consolas", 11f, FontStyle.Bold)
        };
        var detail = new Label
        {
            Text = $"Listening only on 127.0.0.1:{Program.Port}\r\nProtocol {Program.ProtocolVersion}  •  Runtime {Program.Version}\r\nAudio stays local. Skins connect by capability.",
            Location = new Point(33, 156),
            Size = new Size(450, 76)
        };
        Controls.AddRange([title, subtitle, _status, detail]);
    }

    internal void SetServiceStatus(string text, bool online)
    {
        if (InvokeRequired) { BeginInvoke(() => SetServiceStatus(text, online)); return; }
        _status.Text = text;
        _status.ForeColor = online ? Color.FromArgb(200, 255, 25) : Color.FromArgb(255, 177, 43);
    }
}

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SettingsKey = @"Software\Opulent Hardware\Module Runtime";
    private const string ValueName = "OpulentHardwareModuleRuntime";
    private const string LegacyValueName = "OpulentHardwareVolumeMixer";
    private const string StartupPreferenceValue = "StartupPreferenceConfigured";

    internal static void EnsureDefaultEnabled()
    {
        using RegistryKey settings = Registry.CurrentUser.CreateSubKey(SettingsKey, true);
        if (settings.GetValue(StartupPreferenceValue) is not null) return;

        SetEnabled(true);
        settings.SetValue(StartupPreferenceValue, 1, RegistryValueKind.DWord);
    }

    internal static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string || key?.GetValue(LegacyValueName) is string;
    }

    internal static void EnsureCurrentExecutable()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        bool enabled = key.GetValue(ValueName) is string || key.GetValue(LegacyValueName) is string;
        if (!enabled) return;
        string executable = Environment.ProcessPath ?? Application.ExecutablePath;
        key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
        key.DeleteValue(LegacyValueName, false);
    }

    internal static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        using RegistryKey settings = Registry.CurrentUser.CreateSubKey(SettingsKey, true);
        settings.SetValue(StartupPreferenceValue, 1, RegistryValueKind.DWord);
        if (!enabled) { key.DeleteValue(ValueName, false); key.DeleteValue(LegacyValueName, false); return; }
        string executable = Environment.ProcessPath ?? Application.ExecutablePath;
        key.DeleteValue(LegacyValueName, false);
        key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
    }
}

internal static class BrandIcon
{
    internal static Icon Create()
    {
        var bitmap = new Bitmap(64, 64);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.FromArgb(8, 11, 11));
        using var lime = new Pen(Color.FromArgb(200, 255, 25), 4f) { LineJoin = LineJoin.Bevel };
        using var glow = new SolidBrush(Color.FromArgb(200, 255, 25));
        Point[] hex = [new(32, 5), new(54, 17), new(54, 47), new(32, 59), new(10, 47), new(10, 17)];
        graphics.DrawPolygon(lime, hex);
        graphics.FillRectangle(glow, 19, 36, 6, 12);
        graphics.FillRectangle(glow, 29, 24, 6, 24);
        graphics.FillRectangle(glow, 39, 15, 6, 33);
        IntPtr handle = bitmap.GetHicon();
        return (Icon)Icon.FromHandle(handle).Clone();
    }
}

internal sealed record ApiError(string Code, string Message);
internal sealed record ChannelUpdate(float? Volume, bool? Muted);
internal sealed record OutputSelection(string DeviceId);
internal sealed record RuntimeHandshake(string ClientId, string? ClientVersion, string[]? RequiredCapabilities);
internal sealed record MediaCommand(string Command, long? OffsetMs);
