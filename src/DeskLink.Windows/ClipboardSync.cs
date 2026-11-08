using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace DeskLink;

internal sealed class ClipboardSync
{
    private readonly LinkClient _link;
    private readonly ConcurrentQueue<(bool IsImage, string? Text, byte[]? Png)> _incoming = new();
    private string _lastSeen = "";
    private string _lastApplied = "";
    private string _lastImgHash = "";
    private string _lastAppliedImg = "";

    public ClipboardSync(LinkClient link) => _link = link;

    public void Start()
    {
        // Clipboard do Windows (imagem) exige thread STA
        var t = new Thread(Run) { IsBackground = true, Name = "desklink-clip" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private void Run()
    {
        while (true)
        {
            try
            {
                Drain();
                Poll();
            }
            catch (Exception ex)
            {
                Log.Write("clipboard: " + ex.Message);
            }
            Thread.Sleep(300);
        }
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA1.HashData(data));

    // ---- recebido do Linux: aplicado na thread STA ----
    public void Apply(string text) => _incoming.Enqueue((false, text, null));
    public void ApplyImage(byte[] png) => _incoming.Enqueue((true, null, png));

    private void Drain()
    {
        while (_incoming.TryDequeue(out var item))
        {
            try
            {
                if (item.IsImage)
                {
                    using var ms = new MemoryStream(item.Png!);
                    using var img = Image.FromStream(ms);
                    using var bmp = new Bitmap(img);
                    Clipboard.SetImage(bmp);
                    _lastAppliedImg = Hash(item.Png!);
                    _lastImgHash = _lastAppliedImg;
                    Log.Write($"clipboard <- linux (imagem {item.Png!.Length} bytes)");
                }
                else
                {
                    var text = item.Text ?? "";
                    if (text.Length == 0) continue;
                    _lastSeen = text;
                    if (Clipboard.GetText() == text)
                    {
                        _lastApplied = text;
                        continue;
                    }
                    Clipboard.SetText(text);
                    _lastApplied = text;
                    Log.Write($"clipboard <- linux ({text.Length} chars)");
                }
            }
            catch (Exception ex)
            {
                Log.Write("clipboard aplicar: " + ex.Message);
            }
        }
    }

    // ---- enviado para o Linux ----
    private void Poll()
    {
        if (!_link.IsConnected) return;

        try
        {
            if (Clipboard.ContainsImage())
            {
                using var img = Clipboard.GetImage();
                if (img != null)
                {
                    using var ms = new MemoryStream();
                    img.Save(ms, ImageFormat.Png);
                    var png = ms.ToArray();
                    var h = Hash(png);
                    if (h != _lastImgHash && h != _lastAppliedImg)
                    {
                        _lastImgHash = h;
                        _link.Send(Proto.ClipboardImageMsg(png));
                        Log.Write($"clipboard -> linux (imagem {png.Length} bytes)");
                    }
                    return; // imagem tem prioridade sobre texto
                }
            }

            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (string.IsNullOrEmpty(text)) return;
                if (text == _lastApplied)
                {
                    _lastSeen = text;
                    return;
                }
                if (text == _lastSeen) return;

                _lastSeen = text;
                _link.Send(Proto.ClipboardMsg(text));
                Log.Write($"clipboard -> linux ({text.Length} chars)");
            }
        }
        catch (Exception ex)
        {
            Log.Write("clipboard ler: " + ex.Message);
        }
    }
}
