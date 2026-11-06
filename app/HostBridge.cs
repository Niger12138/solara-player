using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Forms;

namespace Solara;

/// <summary>只放行用户通过系统对话框亲手选中的文件，别的路径一律拒绝。</summary>
internal static class MediaAccess
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase);

    public static void Allow(string path)
    {
        try { Allowed.Add(Path.GetFullPath(path)); } catch { }
    }

    public static bool IsAllowed(string path)
    {
        try { return Allowed.Contains(Path.GetFullPath(path)); } catch { return false; }
    }
}

/// <summary>
/// 暴露给网页的原生能力：用自己的文件夹/文件选择器，取代 Chromium 那个
/// 标题写着「选择要上传的文件夹」的对话框。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class HostBridge
{
    private static readonly string[] AudioExt =
    {
        ".mp3", ".mp2", ".flac", ".wav", ".wave", ".ogg", ".oga", ".opus", ".m4a", ".m4b",
        ".aac", ".weba", ".webm", ".aif", ".aiff", ".mka", ".ape", ".wma", ".alac",
        ".mpc", ".wv", ".amr", ".m4r", ".ac3", ".dts", ".mid", ".midi"
    };

    private const int MaxFiles = 20000;
    private readonly Form? _owner;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public HostBridge() { }
    public HostBridge(Form owner) { _owner = owner; }

    /* ---------------- 对外方法 ---------------- */

    /// <summary>按绝对路径直接扫描（脚本 / 排障用；正常使用走系统对话框）。</summary>
    public string ScanFolderPath(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return ErrorJson("目录不存在: " + path);
            return ScanFolder(path);
        }
        catch (Exception ex)
        {
            Log.Write("ScanFolderPath failed: " + ex);
            return ErrorJson(ex.Message);
        }
    }

    public string PickFolder()
    {
        try
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择音乐文件夹（自动扫描子目录，同名 .lrc 歌词一起带上）",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            var last = Store.Get("lastFolder");
            if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) dlg.SelectedPath = last;

            var result = _owner != null ? dlg.ShowDialog(_owner) : dlg.ShowDialog();
            if (result != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath))
                return CancelJson();

            var root = dlg.SelectedPath;
            Store.Set("lastFolder", root);
            return ScanFolder(root);
        }
        catch (Exception ex)
        {
            Log.Write("PickFolder failed: " + ex);
            return ErrorJson(ex.Message);
        }
    }

    public string PickFiles()
    {
        try
        {
            using var dlg = new OpenFileDialog
            {
                Title = "选择音频文件",
                Multiselect = true,
                Filter = "音频文件|*.mp3;*.flac;*.wav;*.ogg;*.opus;*.m4a;*.aac;*.wma;*.aiff;*.aif;*.ape;*.alac|所有文件|*.*"
            };
            var last = Store.Get("lastFileDir");
            if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) dlg.InitialDirectory = last;

            var result = _owner != null ? dlg.ShowDialog(_owner) : dlg.ShowDialog();
            if (result != DialogResult.OK || dlg.FileNames.Length == 0)
                return CancelJson();

            var files = dlg.FileNames;
            try { Store.Set("lastFileDir", Path.GetDirectoryName(files[0]) ?? ""); } catch { }

            var list = new List<object>();
            var lyrics = new List<object>();
            foreach (var f in files.Take(MaxFiles))
            {
                if (!File.Exists(f)) continue;
                var fi = new FileInfo(f);
                if (!AudioExt.Contains(fi.Extension.ToLowerInvariant()) && !fi.Extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (fi.Extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase))
                {
                    lyrics.Add(new { k = Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), t = ReadTextSafe(f) });
                    continue;
                }

                MediaAccess.Allow(f);
                var meta = MediaInfo.Read(f, fi.Length, wantCover: false);

                list.Add(new
                {
                    p = f,
                    r = fi.Name,
                    s = fi.Length,
                    t = meta.Title,
                    a = meta.Artist,
                    al = meta.Album,
                    y = meta.Year,
                    d = Math.Round(meta.Duration, 2),
                    c = meta.HasCover
                });
                var lrc = Path.Combine(fi.DirectoryName ?? "", fi.Name.Substring(0, fi.Name.Length - fi.Extension.Length) + ".lrc");
                if (File.Exists(lrc))
                {
                    lyrics.Add(new { k = fi.Name.Substring(0, fi.Name.Length - fi.Extension.Length).ToLowerInvariant(), t = ReadTextSafe(lrc) });
                }
            }

            return JsonSerializer.Serialize(new { ok = true, root = "", files = list, lyrics }, JsonOpts);
        }
        catch (Exception ex)
        {
            Log.Write("PickFiles failed: " + ex);
            return ErrorJson(ex.Message);
        }
    }

    /* ---------------- 目录扫描 ---------------- */

    private string ScanFolder(string root)
    {
        var files = new List<object>();
        var lyrics = new List<object>();
        var lyricSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        var dirCount = 0;
        var seenDir = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0 && count < MaxFiles && dirCount < 20000)
        {
            var dir = stack.Pop();
            if (!seenDir.Add(dir)) continue;
            dirCount++;

            string[] entries;
            try { entries = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var f in entries)
            {
                if (count >= MaxFiles) break;
                var ext = Path.GetExtension(f).ToLowerInvariant();
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');

                if (ext == ".lrc")
                {
                    var key = rel.Substring(0, rel.Length - 4).ToLowerInvariant();
                    if (lyricSeen.Add(key) && lyrics.Count < MaxFiles)
                        lyrics.Add(new { k = key, t = ReadTextSafe(f) });
                    continue;
                }
                if (!AudioExt.Contains(ext)) continue;

                FileInfo fi;
                try { fi = new FileInfo(f); } catch { continue; }
                MediaAccess.Allow(f);

                var meta = MediaInfo.Read(f, fi.Length, wantCover: false);
                files.Add(new
                {
                    p = f,
                    r = rel,
                    s = fi.Length,
                    t = meta.Title,
                    a = meta.Artist,
                    al = meta.Album,
                    y = meta.Year,
                    d = Math.Round(meta.Duration, 2),
                    c = meta.HasCover // 只标记有没有封面，数据在播放时按需取
                });
                count++;
            }

            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    // 跳过符号链接 / 联接点，避免目录环导致无限递归
                    try
                    {
                        var di = new DirectoryInfo(sub);
                        if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push(sub);
                }
            }
            catch { }
        }

        Log.Write("scan " + root + " -> " + files.Count + " files, " + dirCount + " dirs");

        return JsonSerializer.Serialize(new
        {
            ok = true,
            root,
            files,
            lyrics,
            truncated = count >= MaxFiles
        }, JsonOpts);
    }

    private static string ReadTextSafe(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            // 兼容 UTF-8 BOM / GBK 以外的常见情况
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding("GB18030").GetString(bytes);
            }
        }
        catch
        {
            return "";
        }
    }

    private static string CancelJson() => "{\"ok\":false,\"cancel\":true}";
    private static string ErrorJson(string msg) =>
        JsonSerializer.Serialize(new { ok = false, error = msg }, JsonOpts);
}

/// <summary>把一个小配置写到 %LOCALAPPDATA%\SOLARA\settings.json（不可写就静默放弃）。</summary>
internal static class Store
{
    private static Dictionary<string, string>? _cache;

    private static string FilePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SOLARA");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    private static Dictionary<string, string> Load()
    {
        if (_cache != null) return _cache;
        _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = FilePath();
            if (!File.Exists(path)) return _cache;
            var doc = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (doc != null) _cache = new Dictionary<string, string>(doc, StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return _cache;
    }

    public static string Get(string key) => Load().TryGetValue(key, out var v) ? v : "";

    public static void Set(string key, string value)
    {
        try
        {
            var map = Load();
            map[key] = value;
            File.WriteAllText(FilePath(), JsonSerializer.Serialize(map));
        }
        catch { }
    }
}

/// <summary>把解析出来的内嵌封面暂存起来，页面用 https://app.local/cover?p=... 取。</summary>
internal static class CoverStore
{
    private static readonly Dictionary<string, (byte[] Data, string Mime)> Map =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _bytes;
    private const long MaxTotal = 128L * 1024 * 1024;

    public static void Put(string path, byte[]? data, string mime)
    {
        if (data == null || data.Length == 0) return;
        if (data.Length > 8 * 1024 * 1024) return;
        if (_bytes + data.Length > MaxTotal) return;
        if (Map.ContainsKey(path)) return;
        Map[path] = (data, string.IsNullOrEmpty(mime) ? "image/jpeg" : mime);
        _bytes += data.Length;
    }

    public static bool TryGet(string path, out byte[] data, out string mime)
    {
        if (Map.TryGetValue(path, out var v))
        {
            data = v.Data;
            mime = v.Mime;
            return true;
        }
        data = Array.Empty<byte>();
        mime = "";
        return false;
    }
}

/// <summary>把 FileStream 裁剪成指定长度，供 HTTP Range 响应用。</summary>
internal sealed class RangeStream : Stream
{
    private readonly Stream _inner;
    private long _remaining;

    public RangeStream(Stream inner, long length)
    {
        _inner = inner;
        _remaining = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => 0;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0) return 0;
        var want = (int)Math.Min(count, _remaining);
        var n = _inner.Read(buffer, offset, want);
        _remaining -= n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _inner.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
