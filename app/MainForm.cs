using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Solara;

internal sealed class MainForm : Form
{
    private const string Host = "https://app.local/";
    private const string HostRoot = "https://app.local/";

    private readonly WebView2 _web = new();
    private bool _ready;
    private int _crashCount;

    public MainForm()
    {
        Log.Write("MainForm ctor begin");
        Text = "SOLARA 音频播放器";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1180, 760);
        BackColor = Color.FromArgb(8, 7, 14);
        DoubleBuffered = true;
        KeyPreview = true;
        // 整个 UI 在 WebView2 里，WinForms 不需要再做一次 DPI 缩放，
        // 否则 Size 会被乘上缩放倍数导致窗口超出屏幕。
        AutoScaleMode = AutoScaleMode.None;

        try
        {
            var ico = LoadResourceBytes("app.ico");
            if (ico != null) Icon = new Icon(new MemoryStream(ico));
        }
        catch
        {
            // 图标缺失不影响使用
        }

        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Color.FromArgb(8, 7, 14);
        _web.AllowExternalDrop = true;
        Controls.Add(_web);

        Shown += async (_, _) =>
        {
            ApplyInitialBounds();
            await BootAsync();
        };
        Resize += (_, _) => PushWindowState();
        FormClosed += (_, _) =>
        {
            try { _web.Dispose(); } catch { }
        };
        Log.Write("MainForm ctor end");
    }

    /* ---------------- 启动 ---------------- */

    /// <summary>
    /// 用 Win32 物理像素确定初始位置与大小，避免 WinForms 在 150% 缩放下把 Size 再放大一次。
    /// </summary>
    private void ApplyInitialBounds()
    {
        try
        {
            var wa = Screen.FromHandle(Handle).WorkingArea;
            var w = Math.Max(1200, (int)(wa.Width * 0.92));
            var h = Math.Max(760, (int)(wa.Height * 0.90));
            w = Math.Min(w, wa.Width);
            h = Math.Min(h, wa.Height);
            NativeMethods.SetWindowPos(Handle, IntPtr.Zero,
                wa.X + (wa.Width - w) / 2,
                wa.Y + (wa.Height - h) / 2,
                w, h,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            Log.Write("bounds " + w + "x" + h + " in workarea " + wa.Width + "x" + wa.Height + " dpi=" + DeviceDpi);
        }
        catch (Exception ex)
        {
            Log.Write("ApplyInitialBounds failed: " + ex.Message);
        }
    }

    private async Task BootAsync()
    {
        Log.Write("BootAsync start");
        var dataDir = ResolveDataDir();

        var browserArgs =
            "--autoplay-policy=no-user-gesture-required " +
            "--disable-features=CalculateNativeWinOcclusion " +
            "--disable-background-timer-throttling " +
            "--disable-renderer-backgrounding " +
            "--disable-backgrounding-occluded-windows";

        // 开发/排障用：设置 SOLARA_DEBUG_PORT 环境变量才开放远程调试端口
        var debugPort = Environment.GetEnvironmentVariable("SOLARA_DEBUG_PORT");
        if (!string.IsNullOrWhiteSpace(debugPort))
        {
            browserArgs += " --remote-debugging-port=" + debugPort.Trim();
        }

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = browserArgs
        };
        if (Environment.GetEnvironmentVariable("SOLARA_SAFE_MODE") == "1")
        {
            options = null;
            Log.Write("safe mode: no browser arguments");
        }

        CoreWebView2Environment? env = null;
        try
        {
            env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
            Log.Write("env created");
        }
        catch (Exception ex)
        {
            Log.Write("environment failed: " + ex);
            MessageBox.Show(this,
                "没有找到 WebView2 运行时，播放器无法启动。\n\n" +
                "安装「Microsoft Edge WebView2 Runtime」后重试（Windows 11 通常已自带）。\n\n" +
                ex.Message,
                "SOLARA", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        // 无边框窗口 + PerMonitorV2 下，必须确保控件自己的 HWND 已经建好
        try { _web.CreateControl(); } catch { }
        Log.Write("webview hwnd=" + _web.Handle.ToInt64());

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _web.EnsureCoreWebView2Async(env);
                lastError = null;
                Log.Write("webview ensured (attempt " + attempt + ")");
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log.Write("ensure attempt " + attempt + " failed: " + ex.Message);
                await Task.Delay(500);
            }
        }

        if (lastError != null)
        {
            Log.Write("WebView2 init failed: " + lastError);
            MessageBox.Show(this,
                "WebView2 初始化失败。\n\n" + lastError.Message,
                "SOLARA", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        // 让页面按系统 DPI 缩放渲染（150% 屏幕上界面才不会被缩成一小块）
        ApplyRasterScale(DeviceDpi);

        var core = _web.CoreWebView2;
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreDevToolsEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsWebMessageEnabled = true;
        s.IsBuiltInErrorPageEnabled = false;

        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += OnProcessFailed;
        try
        {
            core.AddHostObjectToScript("solara", new HostBridge(this));
            Log.Write("host object registered");
        }
        catch (Exception ex)
        {
            Log.Write("host object failed: " + ex.Message);
        }
        core.NavigationCompleted += (_, e) =>
        {
            Log.Write("NavigationCompleted ok=" + e.IsSuccess + " status=" + e.WebErrorStatus);
            if (e.IsSuccess)
            {
                _ready = true;
                PushWindowState();
            }
        };
        core.AddWebResourceRequestedFilter(HostRoot + "*", CoreWebView2WebResourceContext.All);
        Log.Write("navigate -> " + HostRoot + "index.html");
        core.Navigate(HostRoot + "index.html");
    }

    private void ApplyRasterScale(int dpi)
    {
        try
        {
            // WebView2 自己会按窗口 DPI 渲染（CSS 视口 = 物理尺寸 / 缩放比），
            // 这里不要再叠加 ZoomFactor，否则会变成双倍放大。
            _web.ZoomFactor = 1.0;
            Log.Write("zoomFactor=1.0 dpi=" + dpi);
        }
        catch (Exception ex)
        {
            Log.Write("zoom failed: " + ex.Message);
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyRasterScale(e.DeviceDpiNew);
    }

    /* ---------------- 内嵌资源 ---------------- */
    /// <summary>
    /// WebView2 的 user data folder 必须真实可写，否则 CreateCoreWebView2Controller
    /// 会直接返回 ERROR_BUSY(0x800700AA)。这里按便携优先的顺序逐个试。
    /// </summary>
    private static string ResolveDataDir()
    {
        var candidates = new List<string>();
        try { candidates.Add(Path.Combine(AppContext.BaseDirectory, "WebView2Data")); } catch { }
        try
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SOLARA", "WebView2"));
        }
        catch { }
        try { candidates.Add(Path.Combine(Path.GetTempPath(), "SOLARA-WebView2")); } catch { }

        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                Log.Write("dataDir ok: " + dir);
                return dir;
            }
            catch (Exception ex)
            {
                Log.Write("dataDir rejected: " + dir + " :: " + ex.Message);
            }
        }

        Log.Write("dataDir fallback to base dir");
        return Path.Combine(AppContext.BaseDirectory, "WebView2Data");
    }

    private static byte[]? LoadResourceBytes(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(name);
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var raw = e.Request.Uri;
            if (!raw.StartsWith(HostRoot, StringComparison.OrdinalIgnoreCase)) return;

            var path = raw.Substring(HostRoot.Length);
            var cut = path.IndexOfAny(new[] { '?', '#' });
            var query = "";
            if (cut >= 0)
            {
                var qIndex = path.IndexOf('?');
                var hIndex = path.IndexOf('#');
                var end = hIndex >= 0 ? hIndex : path.Length;
                if (qIndex >= 0 && qIndex < end) query = path.Substring(qIndex + 1, end - qIndex - 1);
                path = path.Substring(0, cut);
            }

            if (path.Equals("f", StringComparison.OrdinalIgnoreCase))
            {
                ServeMedia(e, query);
                return;
            }

            if (path.Equals("cover", StringComparison.OrdinalIgnoreCase))
            {
                ServeCover(e, query);
                return;
            }

            if (path.Length == 0 || path.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            {
                var html = LoadResourceBytes("app.html");
                if (html == null)
                {
                    Respond(e, 500, "Internal Error", "text/plain", Array.Empty<byte>());
                    return;
                }
                Respond(e, 200, "OK", "text/html; charset=utf-8", html);
                return;
            }

            Respond(e, 404, "Not Found", "text/plain", Array.Empty<byte>());
        }
        catch
        {
            // 请求处理期间的异常不应该让应用崩掉
        }
    }

    private void Respond(CoreWebView2WebResourceRequestedEventArgs e, int status, string reason,
        string contentType, byte[] body)
    {
        var stream = new MemoryStream(body, writable: false);
        var headers = "Content-Type: " + contentType + "\r\nCache-Control: no-store\r\n";
        e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(stream, status, reason, headers);
    }

    /* ---------------- 本地音频路由（支持 Range，可拖动进度） ---------------- */

    private void ServeMedia(CoreWebView2WebResourceRequestedEventArgs e, string query)
    {
        var env = _web.CoreWebView2.Environment;
        try
        {
            var enc = GetQueryValue(query, "p");
            if (string.IsNullOrEmpty(enc))
            {
                Respond(e, 400, "Bad Request", "text/plain", Array.Empty<byte>());
                return;
            }

            string file;
            try { file = DecodeB64Url(enc); }
            catch
            {
                Respond(e, 400, "Bad Request", "text/plain", Array.Empty<byte>());
                return;
            }

            if (!MediaAccess.IsAllowed(file) || !File.Exists(file))
            {
                Log.Write("media denied: " + file);
                Respond(e, 403, "Forbidden", "text/plain", Array.Empty<byte>());
                return;
            }

            var total = new FileInfo(file).Length;
            long start = 0, end = total - 1;
            var partial = false;

            var rangeHeader = "";
            try { rangeHeader = e.Request.Headers.GetHeader("Range") ?? ""; } catch { }

            if (rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var spec = rangeHeader.Substring(6).Split(',')[0].Trim();
                var dash = spec.IndexOf('-');
                if (dash >= 0)
                {
                    var a = spec.Substring(0, dash);
                    var b = spec.Substring(dash + 1);
                    if (long.TryParse(a, out var s1)) start = s1;
                    if (long.TryParse(b, out var e1)) end = e1;
                    if (start < 0) start = 0;
                    if (end < 0 || end >= total) end = total - 1;
                    if (start > end)
                    {
                        Respond(e, 416, "Range Not Satisfiable", "text/plain", Array.Empty<byte>());
                        return;
                    }
                    partial = true;
                }
            }

            var length = end - start + 1;
            var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, false);
            if (start > 0) fs.Seek(start, SeekOrigin.Begin);

            var headers = "Content-Type: " + MimeFor(file) + "\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\n";
            if (partial)
                headers += "Content-Range: bytes " + start + "-" + end + "/" + total + "\r\n";
            headers += "Content-Length: " + length + "\r\n";
            var stream = new RangeStream(fs, length);

            if (partial)
            {
                e.Response = env.CreateWebResourceResponse(stream, 206, "Partial Content", headers);
            }
            else
            {
                e.Response = env.CreateWebResourceResponse(stream, 200, "OK", headers);
            }
        }
        catch (Exception ex)
        {
            Log.Write("ServeMedia failed: " + ex.Message);
            try { Respond(e, 500, "Internal Error", "text/plain", Array.Empty<byte>()); } catch { }
        }
    }

    private void ServeCover(CoreWebView2WebResourceRequestedEventArgs e, string query)
    {
        try
        {
            var enc = GetQueryValue(query, "p");
            if (string.IsNullOrEmpty(enc))
            {
                Respond(e, 400, "Bad Request", "text/plain", Array.Empty<byte>());
                return;
            }

            string file;
            try { file = DecodeB64Url(enc); }
            catch
            {
                Respond(e, 400, "Bad Request", "text/plain", Array.Empty<byte>());
                return;
            }

            if (!MediaAccess.IsAllowed(file) || !File.Exists(file))
            {
                Respond(e, 403, "Forbidden", "text/plain", Array.Empty<byte>());
                return;
            }

            if (!CoverStore.TryGet(file, out var data, out var mime))
            {
                var info = MediaInfo.Read(file, new FileInfo(file).Length);
                if (info.Cover == null)
                {
                    Respond(e, 404, "Not Found", "text/plain", Array.Empty<byte>());
                    return;
                }
                data = info.Cover;
                mime = info.CoverMime;
                CoverStore.Put(file, data, mime);
            }

            var stream = new MemoryStream(data, writable: false);
            var headers = "Content-Type: " + mime + "\r\nCache-Control: public, max-age=86400\r\n";
            e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
        }
        catch (Exception ex)
        {
            Log.Write("ServeCover failed: " + ex.Message);
            try { Respond(e, 500, "Internal Error", "text/plain", Array.Empty<byte>()); } catch { }
        }
    }

    private static string GetQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return "";
        foreach (var part in query.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part.Substring(0, eq).Equals(key, StringComparison.OrdinalIgnoreCase))
                return part.Substring(eq + 1);
        }
        return "";
    }

    private static string DecodeB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    private static string MimeFor(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".mp3": return "audio/mpeg";
            case ".m4a":
            case ".m4b":
            case ".aac": return "audio/mp4";
            case ".flac": return "audio/flac";
            case ".wav":
            case ".wave": return "audio/wav";
            case ".ogg":
            case ".oga": return "audio/ogg";
            case ".opus": return "audio/ogg; codecs=opus";
            case ".weba": return "audio/webm";
            case ".webm": return "audio/webm";
            case ".aif":
            case ".aiff": return "audio/aiff";
            case ".wma": return "audio/x-ms-wma";
            default: return "application/octet-stream";
        }
    }

    /* ---------------- 窗口消息桥 ---------------- */

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg;
        try { msg = e.TryGetWebMessageAsString(); }
        catch { return; }

        switch (msg)
        {
            case "drag":
                StartWindowDrag();
                break;
            case "min":
                WindowState = FormWindowState.Minimized;
                break;
            case "max":
                ToggleMaximize();
                break;
            case "close":
                Close();
                break;
        }
    }

    private void StartWindowDrag()
    {
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN,
            (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
    }

    private void ToggleMaximize()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
        }
        else
        {
            MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
            WindowState = FormWindowState.Maximized;
        }
        PushWindowState();
    }

    private void PushWindowState()
    {
        if (!_ready || _web.CoreWebView2 == null) return;
        try
        {
            _web.CoreWebView2.PostWebMessageAsString(
                WindowState == FormWindowState.Maximized ? "maximized" : "restored");
        }
        catch
        {
            // 窗口销毁过程中忽略
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (++_crashCount > 2) return;
        _ready = false;
        try { _web.CoreWebView2?.Reload(); } catch { }
    }

    /* ---------------- 窗口行为 ---------------- */

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
            NativeMethods.ApplyRoundCorners(Handle);
        }
        catch { }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_SYSCOMMAND &&
            ((int)m.WParam & 0xFFF0) == NativeMethods.SC_MAXIMIZE)
        {
            try { MaximizedBounds = Screen.FromHandle(Handle).WorkingArea; } catch { }
        }
        base.WndProc(ref m);
    }
}
