using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Solara;

/// <summary>
/// 直接在 C# 里读文件头拿标题/艺术家/专辑/时长/封面。
/// 这样浏览器端不需要为每首歌发请求、也不需要创建成百上千个 Audio 元素探测时长。
/// </summary>
internal static class MediaInfo
{
    private const int MaxHead = 128 * 1024;

    public sealed class Info
    {
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public string Year = "";
        public double Duration;
        public bool HasCover;
        public byte[]? Cover;
        public string CoverMime = "";
    }

    /// <summary>
    /// wantCover=false 时跳过封面解析（扫描大目录时只读标签和时长，快 3-5 倍）。
    /// </summary>
    public static Info Read(string path, long size, bool wantCover = true)
    {
        var info = new Info();
        byte[] head;
        int read;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = (int)Math.Min(size > 0 ? size : MaxHead, MaxHead);
            head = new byte[len];
            read = 0;
            while (read < len)
            {
                var n = fs.Read(head, read, len - read);
                if (n <= 0) break;
                read += n;
            }
        }
        catch
        {
            return info;
        }

        try
        {
            if (read >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F')
            {
                ParseWav(head, read, size, info);
            }
            else if (read >= 4 && head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C')
            {
                ParseFlac(head, read, info, wantCover);
            }
            else if (read >= 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
            {
                var tagEnd = ParseId3(head, read, info, wantCover);
                ParseMpegDuration(head, read, tagEnd, size, info);
            }
            else if (read >= 4 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)
            {
                ParseMpegDuration(head, read, 0, size, info);
            }
        }
        catch
        {
            // 解析失败就只保留文件名信息
        }

        return info;
    }

    /* ---------------- WAV ---------------- */

    private static void ParseWav(byte[] b, int len, long size, Info info)
    {
        var p = 12;
        var byteRate = 0;
        long dataSize = 0;

        while (p + 8 <= len)
        {
            var id = Encoding.ASCII.GetString(b, p, 4);
            var sz = BitConverter.ToInt32(b, p + 4);
            if (sz < 0) break;

            if (id == "fmt ")
            {
                if (p + 20 <= len) byteRate = BitConverter.ToInt32(b, p + 16);
            }
            else if (id == "data")
            {
                dataSize = sz;
                break;
            }

            var step = 8 + sz + (sz & 1);
            if (step <= 8) break;
            p += step;
        }

        if (byteRate <= 0) return;
        if (dataSize <= 0 || dataSize > size) dataSize = Math.Max(0, size - 44);
        if (dataSize > 0) info.Duration = (double)dataSize / byteRate;
    }

    /* ---------------- ID3 ---------------- */

    private static int ParseId3(byte[] b, int len, Info info, bool wantCover)
    {
        var major = b[3];
        var tagSize = ((b[6] & 0x7F) << 21) | ((b[7] & 0x7F) << 14) | ((b[8] & 0x7F) << 7) | (b[9] & 0x7F);
        var end = Math.Min(len, tagSize + 10);
        var p = 10;

        if (major == 2)
        {
            while (p + 6 <= end)
            {
                var id = Encoding.ASCII.GetString(b, p, 3);
                if (!IsFrameId(id, 3)) break;
                var sz = (b[p + 3] << 16) | (b[p + 4] << 8) | b[p + 5];
                if (sz <= 0 || p + 6 + sz > end) break;
                ApplyFrame(id, b, p + 6, sz, info, true, wantCover);
                p += 6 + sz;
            }
        }
        else
        {
            var syncsafe = major == 4;
            while (p + 10 <= end)
            {
                var id = Encoding.ASCII.GetString(b, p, 4);
                if (!IsFrameId(id, 4)) break;
                int sz;
                if (syncsafe)
                    sz = ((b[p + 4] & 0x7F) << 21) | ((b[p + 5] & 0x7F) << 14) | ((b[p + 6] & 0x7F) << 7) | (b[p + 7] & 0x7F);
                else
                    sz = (b[p + 4] << 24) | (b[p + 5] << 16) | (b[p + 6] << 8) | b[p + 7];

                if (sz <= 0 || p + 10 + sz > end) break;
                ApplyFrame(id, b, p + 10, sz, info, false, wantCover);
                p += 10 + sz;
            }
        }

        return Math.Min(end, len);
    }

    private static bool IsFrameId(string id, int n)
    {
        if (id.Length != n) return false;
        foreach (var c in id)
        {
            if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return false;
        }
        return true;
    }

    private static void ApplyFrame(string id, byte[] b, int off, int len, Info info, bool v22, bool wantCover)
    {
        switch (id)
        {
            case "TIT2":
            case "TT2":
                info.Title = DecodeText(b, off, len);
                break;
            case "TPE1":
            case "TP1":
                info.Artist = DecodeText(b, off, len);
                break;
            case "TALB":
            case "TAL":
                info.Album = DecodeText(b, off, len);
                break;
            case "TYER":
            case "TDRC":
            case "TYE":
                var y = DecodeText(b, off, len);
                if (y.Length >= 4) info.Year = y.Substring(0, 4);
                break;
            case "APIC":
            case "PIC":
                info.HasCover = true;
                if (wantCover) ParsePicture(b, off, len, info, id == "PIC");
                break;
        }
    }

    private static string DecodeText(byte[] b, int off, int len)
    {
        if (len <= 1 || off + len > b.Length) return "";
        var enc = b[off];
        int start = off + 1, count = len - 1;
        string s;
        try
        {
            if (enc == 0) s = Encoding.Latin1.GetString(b, start, count);
            else if (enc == 1)
            {
                if (count >= 2 && b[start] == 0xFF && b[start + 1] == 0xFE)
                    s = Encoding.Unicode.GetString(b, start + 2, count - 2);
                else if (count >= 2 && b[start] == 0xFE && b[start + 1] == 0xFF)
                    s = Encoding.BigEndianUnicode.GetString(b, start + 2, count - 2);
                else
                    s = Encoding.Unicode.GetString(b, start, count);
            }
            else if (enc == 2) s = Encoding.BigEndianUnicode.GetString(b, start, count);
            else s = Encoding.UTF8.GetString(b, start, count);
        }
        catch
        {
            return "";
        }
        return s.Replace("\0", "").Trim();
    }

    private static void ParsePicture(byte[] b, int off, int len, Info info, bool v22)
    {
        var end = Math.Min(off + len, b.Length);
        var p = off + 1;
        var mime = "";

        if (v22)
        {
            if (p + 3 > end) return;
            var ext = Encoding.ASCII.GetString(b, p, 3).ToLowerInvariant();
            mime = ext == "png" ? "image/png" : "image/jpeg";
            p += 3;
        }
        else
        {
            var mstart = p;
            while (p < end && b[p] != 0) p++;
            if (p > mstart) mime = Encoding.Latin1.GetString(b, mstart, p - mstart);
            p++;
        }

        if (p >= end) return;
        p++; // picture type

        var enc = b[off];
        if (enc == 1 || enc == 2)
        {
            while (p + 1 < end && !(b[p] == 0 && b[p + 1] == 0)) p += 2;
            p += 2;
        }
        else
        {
            while (p < end && b[p] != 0) p++;
            p++;
        }

        var dataLen = end - p;
        if (dataLen < 64) return;

        var data = new byte[dataLen];
        Buffer.BlockCopy(b, p, data, 0, dataLen);

        if (string.IsNullOrEmpty(mime) || !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            mime = Sniff(data) ?? "image/jpeg";

        info.Cover = data;
        info.CoverMime = mime;
    }

    private static string? Sniff(byte[] d)
    {
        if (d.Length < 12) return null;
        if (d[0] == 0xFF && d[1] == 0xD8) return "image/jpeg";
        if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E) return "image/png";
        if (d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46) return "image/gif";
        if (d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50) return "image/webp";
        return null;
    }

    /* ---------------- MPEG 时长 ---------------- */

    private static readonly int[] BitrateV1L3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
    private static readonly int[] BitrateV2L3 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
    private static readonly int[] RatesV1 = { 44100, 48000, 32000, 0 };
    private static readonly int[] RatesV2 = { 22050, 24000, 16000, 0 };
    private static readonly int[] RatesV25 = { 11025, 12000, 8000, 0 };

    private static void ParseMpegDuration(byte[] b, int len, int start, long fileSize, Info info)
    {
        var limit = Math.Min(len, start + 200000);
        for (var i = start; i + 4 < limit; i++)
        {
            if (b[i] != 0xFF || (b[i + 1] & 0xE0) != 0xE0) continue;

            var versionBits = (b[i + 1] >> 3) & 0x03; // 0=2.5 2=2 3=1
            var layerBits = (b[i + 1] >> 1) & 0x03;   // 1=III
            if (layerBits != 1) continue;

            var brIdx = (b[i + 2] >> 4) & 0x0F;
            var srIdx = (b[i + 2] >> 2) & 0x03;
            if (brIdx == 0 || brIdx == 15 || srIdx == 3) continue;

            var isV1 = versionBits == 3;
            var bitrate = (isV1 ? BitrateV1L3 : BitrateV2L3)[brIdx];
            var sampleRate = versionBits switch
            {
                3 => RatesV1[srIdx],
                2 => RatesV2[srIdx],
                _ => RatesV25[srIdx]
            };
            if (bitrate <= 0 || sampleRate <= 0) continue;

            // Xing / Info 头（VBR 精确帧数）
            var xing = IndexOf(b, i, Math.Min(limit, i + 240), "Xing");
            if (xing < 0) xing = IndexOf(b, i, Math.Min(limit, i + 240), "Info");
            if (xing >= 0 && xing + 12 <= len)
            {
                var flags = ReadBe32(b, xing + 4);
                if ((flags & 0x01) != 0)
                {
                    var frames = (long)ReadBe32(b, xing + 8);
                    if (frames > 0)
                    {
                        var spf = isV1 ? 1152 : 576;
                        info.Duration = (double)frames * spf / sampleRate;
                        return;
                    }
                }
            }

            var audioBytes = Math.Max(0, fileSize - start);
            if (audioBytes > 0)
                info.Duration = audioBytes * 8.0 / (bitrate * 1000.0);
            return;
        }
    }

    private static int IndexOf(byte[] b, int start, int end, string ascii)
    {
        for (var i = start; i + ascii.Length <= end; i++)
        {
            var ok = true;
            for (var j = 0; j < ascii.Length; j++)
            {
                if (b[i + j] != ascii[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    private static int ReadBe32(byte[] b, int p) =>
        (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];

    private static int ReadLe32(byte[] b, int p) =>
        b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24);

    /* ---------------- FLAC ---------------- */

    private static void ParseFlac(byte[] b, int len, Info info, bool wantCover)
    {
        var p = 4;
        var guard = 0;

        while (p + 4 <= len && guard++ < 128)
        {
            var last = (b[p] & 0x80) != 0;
            var type = b[p] & 0x7F;
            var size = (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
            var d = p + 4;
            if (size < 0 || d + size > len) break;

            if (type == 0 && size >= 18)
            {
                var sr = (b[d + 10] << 12) | (b[d + 11] << 4) | (b[d + 12] >> 4);
                long total = ((long)(b[d + 13] & 0x0F) << 32)
                           | ((long)b[d + 14] << 24)
                           | ((long)b[d + 15] << 16)
                           | ((long)b[d + 16] << 8)
                           | b[d + 17];
                if (sr > 0 && total > 0) info.Duration = (double)total / sr;
            }
            else if (type == 4)
            {
                ParseVorbisComment(b, d, size, info);
            }
            else if (type == 6)
            {
                info.HasCover = true;
                if (wantCover) ParseFlacPicture(b, d, size, info);
            }

            if (last) break;
            p = d + size;
        }
    }

    private static void ParseVorbisComment(byte[] b, int off, int len, Info info)
    {
        try
        {
            var p = off;
            var end = off + len;
            var vlen = ReadLe32(b, p);
            p += 4 + vlen;
            if (p + 4 > end) return;
            var count = ReadLe32(b, p);
            p += 4;

            for (var i = 0; i < count && i < 256 && p + 4 <= end; i++)
            {
                var clen = ReadLe32(b, p);
                p += 4;
                if (clen < 0 || p + clen > end) break;
                var kv = Encoding.UTF8.GetString(b, p, clen);
                p += clen;

                var eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                var k = kv.Substring(0, eq).ToUpperInvariant();
                var v = kv.Substring(eq + 1).Trim();
                if (v.Length == 0) continue;

                switch (k)
                {
                    case "TITLE": info.Title = v; break;
                    case "ARTIST": info.Artist = v; break;
                    case "ALBUM": info.Album = v; break;
                    case "DATE": if (v.Length >= 4) info.Year = v.Substring(0, 4); break;
                }
            }
        }
        catch { }
    }

    private static void ParseFlacPicture(byte[] b, int off, int len, Info info)
    {
        try
        {
            var p = off;
            var end = off + len;
            p += 4; // picture type
            var mlen = ReadBe32(b, p); p += 4;
            if (mlen < 0 || p + mlen > end) return;
            var mime = Encoding.ASCII.GetString(b, p, mlen);
            p += mlen;
            var dlen = ReadBe32(b, p); p += 4 + dlen;
            var plen = ReadBe32(b, p); p += 4;
            if (plen <= 0 || p + plen > end) return;

            var data = new byte[plen];
            Buffer.BlockCopy(b, p, data, 0, plen);
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mime = Sniff(data) ?? "image/jpeg";
            info.Cover = data;
            info.CoverMime = mime;
        }
        catch { }
    }
}
