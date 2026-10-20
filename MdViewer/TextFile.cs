using System.Text;

namespace MdViewer;

/// <summary>
/// テキストファイルの読み込みと文字コード判定。
/// BOM → UTF-8 (厳密) → Shift_JIS (CP932) の順に試す。
/// 日本の古い .txt / .md が Shift_JIS のまま残っているケースが多いため。
/// </summary>
internal static class TextFile
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static (string Text, string EncodingName) Read(string path)
    {
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[fs.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = fs.Read(bytes, read, bytes.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < bytes.Length) Array.Resize(ref bytes, read);
        }
        return Decode(bytes);
    }

    public static (string Text, string EncodingName) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8 BOM");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");

        try
        {
            return (StrictUtf8.GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            // UTF-8 として不正 → 日本語 Windows の既定 (CP932) とみなす
        }

        try
        {
            var sjis = Encoding.GetEncoding(932);
            return (sjis.GetString(bytes), "Shift_JIS");
        }
        catch
        {
            return (Encoding.UTF8.GetString(bytes), "UTF-8 (置換あり)");
        }
    }
}
