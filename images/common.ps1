# Shared by annotate.ps1 and hero.ps1: banner colours and the mask loader.
Add-Type -AssemblyName System.Drawing
if (-not ('MaskKey' -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class MaskKey {
    // The mask PNG has an opaque grey background; derive alpha from how green each pixel is.
    public static Bitmap KeyOutGrey(string path) {
        var src = new Bitmap(path);
        var bmp = src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format32bppArgb);
        src.Dispose();
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var px = new byte[data.Stride * bmp.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        for (int i = 0; i < px.Length; i += 4) {
            int b = px[i], g = px[i + 1], r = px[i + 2];
            int a = (g - System.Math.Max(r, b)) * 255 / 40;
            px[i + 3] = (byte)System.Math.Max(0, System.Math.Min(255, a));
        }
        Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
        return bmp;
    }
}
'@
}

$ribbonColor = [System.Drawing.Color]::FromArgb(235, 28, 32, 24)
$edgeColor   = [System.Drawing.Color]::FromArgb(255, 110, 190, 30)
$footerColor = [System.Drawing.Color]::FromArgb(255, 28, 32, 24)
$textColor   = [System.Drawing.Color]::White
# Navy margin from the Ecopedia screenshot; pads every homepage image out to 16:9.
$padColor    = [System.Drawing.Color]::FromArgb(255, 19, 25, 35)
