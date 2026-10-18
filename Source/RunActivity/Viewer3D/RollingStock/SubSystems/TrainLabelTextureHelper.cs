using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Xna.Framework.Graphics;
using Orts.Viewer3D;
using Orts.Viewer3D.Common;

public static class TrainLabelTextureHelper
{
    // Cache gotowych tekstur w pamięci RAM (klucz: hash treści + wymiary, wartość: obiekt Texture2D)
    private static readonly ConcurrentDictionary<string, Texture2D> TextureCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object TextureGenLock = new();

    // Hash treści (labelText) - 8 hex chars dla krótkiego klucza
    private static string ContentHash(string s)
    {
        using var sha = SHA1.Create();
        var b = Encoding.UTF8.GetBytes(s ?? "");
        var h = sha.ComputeHash(b);
        return BitConverter.ToString(h).Replace("-", "").Substring(0, 8).ToLowerInvariant();
    }

    public static Material CreateLabelMaterial(Viewer viewer, string uniqueBaseName, string labelText, string trainNumber, Material originalMaterial, int width = 512, int height = 128)
    {
        if (viewer == null || originalMaterial == null || viewer.GraphicsDevice == null) return null;

        try
        {
            var contentHash = ContentHash($"{trainNumber}_{labelText}");
            var cacheKey = $"{uniqueBaseName}_{width}x{height}_{contentHash}";

            // Sprawdzamy, czy tekstura jest już w pamięci RAM
            if (!TextureCache.TryGetValue(cacheKey, out Texture2D tex) || tex == null || tex.IsDisposed)
            {
                lock (TextureGenLock)
                {
                    if (!TextureCache.TryGetValue(cacheKey, out tex) || tex == null || tex.IsDisposed)
                    {
                        try
                        {
                            // Generujemy teksturę bezpośrednio do strumienia pamięci RAM
                            tex = GenerateLabelTexture(viewer.GraphicsDevice, labelText, trainNumber, width, height);
                            if (tex != null)
                            {
                                TextureCache[cacheKey] = tex;
                            }
                        }
                        catch (Exception ex)
                        {
                            Trace.TraceWarning("GenerateLabelTexture failed: " + ex.Message);
                        }
                    }
                }
            }

            if (tex == null) return null;

            // Klonujemy oryginalny materiał, aby stworzyć niezależną instancję dla tego pojazdu
            var cloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance);
            var mat = cloneMethod?.Invoke(originalMaterial, null) as Material;
            if (mat == null) return null;

            var matType = mat.GetType();

            // Unikalizujemy klucz materiału (Key), aby renderer Open Rails nie grupował pociągów
            try
            {
                var keyField = matType.GetField("Key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (keyField != null)
                {
                    var oldKey = keyField.GetValue(mat)?.ToString() ?? "mat";
                    keyField.SetValue(mat, $"{oldKey}_{Guid.NewGuid()}");
                }
                else
                {
                    var keyProp = matType.GetProperty("Key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (keyProp != null && keyProp.CanWrite)
                    {
                        var oldKey = keyProp.GetValue(mat)?.ToString() ?? "mat";
                        keyProp.SetValue(mat, $"{oldKey}_{Guid.NewGuid()}");
                    }
                }
            }
            catch { }

            // Wstrzykujemy nową, unikalną teksturę do sklonowanego materiału
            foreach (var prop in matType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (prop.CanWrite && (prop.PropertyType == typeof(Texture2D) || prop.PropertyType.Name.Contains("Texture")))
                {
                    try { prop.SetValue(mat, tex); } catch { }
                }
            }

            foreach (var field in matType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType == typeof(Texture2D) || field.FieldType.Name.Contains("Texture"))
                {
                    try { field.SetValue(mat, tex); } catch { }
                }
            }

            try
            {
                var refresh = typeof(Material).GetMethod("RefreshSortingKey", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (refresh != null)
                {
                    refresh.Invoke(mat, new object[] { tex });
                }
                else
                {
                    var keyField = matType.GetField("Key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (keyField != null)
                        keyField.SetValue(mat, $"{keyField.GetValue(mat)}_{Guid.NewGuid()}");
                }
            }
            catch { }

            try { mat.Mark(); } catch { }

            return mat;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("CreateLabelMaterial failed: " + ex.Message);
            return null;
        }
    }

    // Generator zwracający obiekt Texture2D bezpośrednio ze strumienia pamięci RAM
    private static Texture2D GenerateLabelTexture(GraphicsDevice graphicsDevice, string labelText, string trainNumber, int width, int height)
    {
        string destinationText = labelText ?? string.Empty;

        using var bitmap = new Bitmap(width, height);
        float fontSize = bitmap.Height * 0.5f;

        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Black);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using Font font = new Font("Consolas", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using Brush textBrush = new SolidBrush(Color.FromArgb(255, 165, 0)); // Bursztynowy LED

            string trainNumberPart = !string.IsNullOrWhiteSpace(trainNumber) ? trainNumber.Trim() : string.Empty;
            string destinationPart = destinationText;

            using StringFormat verticalCenterFormat = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.None
            };

            float startX = 10f;
            float scaleFactor = 0.6f;

            SizeF numberSize = SizeF.Empty;
            if (!string.IsNullOrEmpty(trainNumberPart))
            {
                numberSize = g.MeasureString(trainNumberPart, font);
            }

            g.ScaleTransform(scaleFactor, 1.0f);

            float currentPixelX = startX;
            if (!string.IsNullOrEmpty(trainNumberPart))
            {
                RectangleF trainNumberRect = new RectangleF(currentPixelX / scaleFactor, 0, numberSize.Width, bitmap.Height);
                g.DrawString(trainNumberPart, font, textBrush, trainNumberRect, verticalCenterFormat);
                currentPixelX += (numberSize.Width * scaleFactor) + 10f;
            }

            float hugeWidth = 5000f;
            RectangleF destinationRect = new RectangleF(currentPixelX / scaleFactor, 0, hugeWidth, bitmap.Height);
            g.DrawString(destinationPart, font, textBrush, destinationRect, verticalCenterFormat);

            g.ResetTransform();
        }

        // Zapisujemy bitmapę do pamięci (MemoryStream) zamiast na dysk twardy
        using (MemoryStream ms = new MemoryStream())
        {
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            return Texture2D.FromStream(graphicsDevice, ms);
        }
    }
}
