using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using SevenZip.Compression.LZ;
using SkiaSharp;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace UnrealPorting.Helpers
{
    public static class PreviewManager
    {
        // ------------------------------------------------------------
        // ENTRY POINT
        // ------------------------------------------------------------
        public static async void ShowFilePreviewAsync(
            TreeViewItem? selectedItem,
            MainWindow mainWindow,
            Func<string, AppPakReader?> readerResolver)
        {
            if (selectedItem?.Tag == null)
            {
                mainWindow.HideSpinner();
                ShowText(mainWindow, "(No file selected)");
                return;
            }

            string filePath = selectedItem.Tag.ToString()!;
            mainWindow.ShowSpinner();
            await Task.Delay(10);

            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                var reader = readerResolver(filePath);

                if (reader == null)
                {
                    mainWindow.HideSpinner();
                    ShowText(mainWindow, $"File not found:\n{filePath}");
                    return;
                }

                switch (ext)
                {
                    case ".uasset":
                    case ".umap":
                        LoadAssetPreview(filePath, reader, mainWindow);
                        break;

                    default:
                        if (!reader.Provider.TryGetGameFile(filePath, out var rawFile) || rawFile == null)
                        {
                            ShowText(mainWindow, $"Game file not found:\n{filePath}");
                            return;
                        }

                        // Correct for your version of CUE4Parse
                        byte[] bytes = rawFile.Read();

                        ShowText(mainWindow, GetHexDump(bytes));
                        break;
                }
            }
            catch (Exception ex)
            {
                mainWindow.HideSpinner();
                ShowText(mainWindow, $"Preview error:\n{ex}");
            }
        }

        // ------------------------------------------------------------
        // ASSET PREVIEW
        // ------------------------------------------------------------
        public static void LoadAssetPreview(string assetPath, AppPakReader reader, MainWindow window)
        {
            if (!reader.Provider.TryGetGameFile(assetPath, out var gameFile) || gameFile == null)
            {
                ShowText(window, $"Game file not found:\n{assetPath}");
                return;
            }

            IPackage? package;
            try
            {
                package = reader.Provider.LoadPackage(gameFile);
            }
            catch (Exception ex)
            {
                ShowText(window, $"Failed to load package:\n{ex.Message}");
                return;
            }

            if (package == null)
            {
                ShowText(window, "Failed to load package.");
                return;
            }

            package.DeserializeAllExports();

            if (package.ExportsLazy.Length == 0)
            {
                ShowText(window, "No exports found.");
                return;
            }

            UObject? export;
            try
            {
                export = package.GetExport(0);
            }
            catch (Exception ex)
            {
                ShowText(window, $"Failed to get export:\n{ex.Message}");
                return;
            }

            if (export == null)
            {
                ShowText(window, "Export was null.");
                return;
            }

            Console.WriteLine("[PREVIEW] Export type = " + export.GetType().Name);

            // ----------------------------------------------------
            // TEXTURE SUPPORT
            // ----------------------------------------------------
            if (export is UTexture2D tex)
            {
                Console.WriteLine("[PREVIEW] Using NuGet conversion decoder");

                Console.WriteLine("=== RAW MIP DEBUG ===");

                try
                {
                    var pd = tex.PlatformData;

                    if (pd == null)
                    {
                        Console.WriteLine("PlatformData = NULL");
                    }
                    else
                    {
                        Console.WriteLine("PlatformData = PRESENT");
                        Console.WriteLine($"FirstMipToSerialize = {pd.FirstMipToSerialize}");
                        Console.WriteLine($"NumMips = {pd.Mips.Length}");

                        for (int i = 0; i < pd.Mips.Length; i++)
                        {
                            var mip = pd.Mips[i];

                            Console.WriteLine($"--- Mip[{i}] ---");

                            if (mip == null)
                            {
                                Console.WriteLine("null mip");
                                continue;
                            }

                            Console.WriteLine($"SizeX = {mip.SizeX}");
                            Console.WriteLine($"SizeY = {mip.SizeY}");
                            Console.WriteLine($"SizeZ = {mip.SizeZ}");

                            if (mip.BulkData == null)
                            {
                                Console.WriteLine("BulkData = NULL");
                                continue;
                            }

                            // BulkData header fields (new format)
                            Console.WriteLine($"BulkData.Header.ElementCount = {mip.BulkData.Header.ElementCount}");

                            bool hasData = mip.BulkData.Data != null;
                            Console.WriteLine($"BulkData.Data != null = {hasData}");

                            if (hasData)
                                Console.WriteLine($"BulkData.Data.Length = {mip.BulkData.Data.Length}");
                            else
                                Console.WriteLine("BulkData.Data.Length = 0");
                        }
                    }
                }
                catch (Exception mipEx)
                {
                    Console.WriteLine("MIP SCAN ERROR: " + mipEx.Message);
                }

                Console.WriteLine("=====================");


                // New CUE4Parse decode: returns CTexture, NOT SKBitmap
                CTexture ctex = tex.Decode();
                SKBitmap? sk = ConvertToSkBitmap(ctex);

                if (sk == null)
                {
                    ShowText(window, "[Texture decode returned null]");
                    return;
                }

                if (sk == null)
                {
                    ShowText(window, "[Texture decode returned null]");
                    return;
                }

                var texInfo = new
                {
                    Type = "Texture2D",
                    Name = tex.Name.ToString(),
                    Class = "UScriptClass'Texture2D'",
                    Flags = export.Flags.ToString().Replace(", ", " | "),

                    Properties = PropertySerializer.SerializeUObject(export)?["Properties"],

                    SizeX = tex.PlatformData?.SizeX,
                    SizeY = tex.PlatformData?.SizeY,
                    ImportedSize = new { X = tex.ImportedSize.X, Y = tex.ImportedSize.Y },
                    PackedData = tex.PlatformData?.PackedData,
                    PixelFormat = tex.Format.ToString(),
                    FirstMipToSerialize = tex.PlatformData?.FirstMipToSerialize,

                    Mips = tex.PlatformData?.Mips?.Select(mip => new {
                        BulkData = mip.BulkData == null ? null : new
                        {
                            BulkDataFlags = mip.BulkData.Header.BulkDataFlags.ToString(),
                            ElementCount = mip.BulkData.Header.ElementCount,
                            SizeOnDisk = mip.BulkData.Header.SizeOnDisk,
                            OffsetInFile = $"0x{mip.BulkData.Header.OffsetInFile:X}"
                        },
                        SizeX = mip.SizeX,
                        SizeY = mip.SizeY,
                        SizeZ = mip.SizeZ,
                    }).ToList()
                };



                // JSON (left pane)
                var json = JsonConvert.SerializeObject(texInfo, Formatting.Indented);

                // PNG (right pane)
                using (var image = sk.Encode(SKEncodedImageFormat.Png, 100))
                {
                    window.HideSpinner();
                    window.ShowDualPane(json, image.ToArray());
                }

                return;
            }

            // ----------------------------------------------------
            // NON-TEXTURES => JSON
            // ----------------------------------------------------
            try
            {
                var json = JsonConvert.SerializeObject(
                    PropertySerializer.SerializeUObject(export),
                    Formatting.Indented);

                window.HideSpinner();
                ShowText(window, json);
            }
            catch (Exception ex)
            {
                window.HideSpinner();
                ShowText(window, $"Failed to serialize export:\n{ex.Message}");
            }
        }

        // ------------------------------------------------------------
        // HEX DUMP
        // ------------------------------------------------------------
        private static string GetHexDump(byte[] data, int max = 512)
        {
            var sb = new StringBuilder();
            int len = Math.Min(data.Length, max);

            for (int i = 0; i < len; i += 16)
            {
                var chunk = data.Skip(i).Take(16).ToArray();
                sb.Append(i.ToString("X4")).Append(": ");
                sb.Append(string.Join(" ", chunk.Select(b => b.ToString("X2"))));
                sb.Append(" | ");
                sb.Append(new string(chunk.Select(b => b >= 32 && b <= 126 ? (char)b : '.').ToArray()));
                sb.AppendLine();
            }

            if (data.Length > max)
                sb.AppendLine($"... ({data.Length - max} more bytes)");

            return sb.ToString();
        }

        // ------------------------------------------------------------ //
        //                  TEXTURE MIP EXPORTER                        //
        // ------------------------------------------------------------ //
        public static void ExportSingleTextureMip(UTexture2D tex, MainWindow window)
        {
            try
            {
                var pd = tex.PlatformData;
                if (pd == null || pd.Mips == null || pd.Mips.Length == 0)
                {
                    ToastManager.ShowToast(window, "Texture has no mip data.", ToastType.Error);
                    return;
                }

                // Build a simple list of mip options for the user
                // Build list of mip sizes for the picker window
                var mipSizes = pd.Mips
                    .Select(m => (m.SizeX, m.SizeY))
                    .ToArray();

                // Show the mip selection window
                var picker = new MipSelectWindow(mipSizes)
                {
                    Owner = window
                };

                bool? result = picker.ShowDialog();
                if (result != true || picker.SelectedMip < 0)
                {
                    ToastManager.ShowToast(window,"Mip export cancelled.", ToastType.Info);
                    return;
                }

                int mipIndex = picker.SelectedMip;
                var mip = pd.Mips[mipIndex];

                // Decode the FULL texture once
                CTexture fullTex = tex.Decode();
                SKBitmap fullBmp = ConvertToSkBitmap(fullTex);

                // Target size = mip size (clamped to something sane)
                int targetW = Math.Max(1, Math.Min(fullBmp.Width, mip.SizeX));
                int targetH = Math.Max(1, Math.Min(fullBmp.Height, mip.SizeY));

                // Resize down to that mip resolution
                var resized = fullBmp.Resize(
                    new SKImageInfo(targetW, targetH),
                    SKFilterQuality.High
                );

                if (resized == null)
                {
                    ToastManager.ShowToast(window, "Failed to resize bitmap for mip export.", ToastType.Error);
                    return;
                }

                // Ask where to save
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = $"{tex.Name}_Mip{mipIndex}_{targetW}x{targetH}.png",
                    Filter = "PNG Image|*.png"
                };

                if (dlg.ShowDialog() != true)
                    return;

                using var image = resized.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(dlg.FileName, image.ToArray());

                string outPath = dlg.FileName;
                ToastManager.ShowToast(
                    window, $"Exported {tex.Name} Mip {mipIndex} ({targetW}x{targetH})", ToastType.Success, outPath
                );
            }
            catch (Exception ex)
            {
                ToastManager.ShowToast(window, $"Export mip failed:\n{ex}", ToastType.Error);
            }
        }


        // ------------------------------------------------------------
        // UI HELPERS
        // ------------------------------------------------------------

        private static SKBitmap ConvertToSkBitmap(CTexture tex)
        {
            // tex.Data contains raw RGBA8 or BGRA8 depending on CUE4Parse output
            // Most newer UE4/UE5 exports are BGRA8

            var bmp = new SKBitmap(tex.Width, tex.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

            unsafe
            {
                fixed (byte* ptr = tex.Data)
                {
                    bmp.InstallPixels(bmp.Info, (IntPtr)ptr, bmp.Info.RowBytes);
                }
            }

            return bmp;
        }
        private static void ShowText(MainWindow mainWindow, string text)
        {
            mainWindow.HideSpinner();
            mainWindow.ShowSinglePaneText(text);
        }

        public static void DeserializeAllExports(this IPackage package)
        {
            if (!package.CanDeserialize) return;

            foreach (var lazy in package.ExportsLazy)
            {
                try { _ = lazy.Value; }
                catch { }
            }
        }

        public static void ExportJsonFromAsset(string assetPath, MainWindow window, AppPakReader reader)
        {
            try
            {
                if (!reader.Provider.TryGetGameFile(assetPath, out var gameFile))
                {
                    ToastManager.ShowToast(window, $"Could not load file: {assetPath}", ToastType.Error);
                    return;
                }

                IPackage? package = reader.Provider.LoadPackage(gameFile);
                if (package == null)
                {
                    ToastManager.ShowToast(window, "Failed to load package.", ToastType.Error);
                    return;
                }

                package.DeserializeAllExports();
                var export = package.GetExport(0);
                if (export == null)
                {
                    ToastManager.ShowToast(window, "Export was null.", ToastType.Error);
                    return;
                }

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = System.IO.Path.GetFileNameWithoutExtension(assetPath) + ".json",
                    Filter = "JSON Files (*.json)|*.json"
                };

                if (dialog.ShowDialog() != true)
                    return;

                string outputPath = dialog.FileName;

                var json = Newtonsoft.Json.JsonConvert.SerializeObject(
                    PropertySerializer.SerializeUObject(export),
                    Newtonsoft.Json.Formatting.Indented);

                File.WriteAllText(outputPath, json);

                // Show JSON after export
                // Notify user
                ToastManager.ShowToast(window, "JSON exported successfully.", ToastType.Success, outputPath);

                // And still show the JSON in the preview panel
                window.ShowSinglePaneText(json);
            }
            catch (Exception ex)
            {
                ToastManager.ShowToast(window, $"Export JSON failed:\n{ex}", ToastType.Error);
            }
        }
        public static void ExportTexturesFromAsset(string assetPath, MainWindow window, AppPakReader reader)
        {
            try
            {
                if (!reader.Provider.TryGetGameFile(assetPath, out var gameFile))
                {
                    ToastManager.ShowToast(window, $"Could not load file: {assetPath}", ToastType.Error);
                    return;
                }

                IPackage package = reader.Provider.LoadPackage(gameFile);
                package.DeserializeAllExports();

                var export = package.GetExport(0);

                var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Select folder to export textures"
                };

                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;

                string outDir = dialog.SelectedPath;

                // Case 1: single texture
                if (export is UTexture2D tex)
                {
                    ExportSingleTexture(tex, outDir);
                    ToastManager.ShowToast(window, $"Exported texture:\n{tex.Name}.png", ToastType.Success, outDir);
                    return;
                }

                // Case 2: material → referenced textures
                if (export is UMaterial mat)
                {
                    List<UTexture2D> textures = GetReferencedTextures(mat);

                    foreach (var t in textures)
                        ExportSingleTexture(t, outDir);

                    ToastManager.ShowToast(window, $"Exported {textures.Count} textures from material:\n{mat.Name}", ToastType.Success, outDir);
                    return;
                }

                ToastManager.ShowToast(window, "No textures found in this asset.", ToastType.Info);
            }
            catch (Exception ex)
            {
                ToastManager.ShowToast(window, $"Export textures failed:\n{ex}", ToastType.Error);
            }
        }

        private static void ExportSingleTexture(UTexture2D tex, string outDir)
        {
            try
            {
                CTexture ctex = tex.Decode();
                SKBitmap? bmp = ConvertToSkBitmap(ctex);

                if (bmp == null)
                {
                    Console.WriteLine("[EXPORT] Decode returned null");
                    return;
                }

                string name = tex.Name + ".png";
                string path = System.IO.Path.Combine(outDir, name);

                using var image = bmp.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(path, image.ToArray());

                Console.WriteLine($"[EXPORT] {name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXPORT] Texture failed: " + ex.Message);
            }
        }
        private static List<UTexture2D> GetReferencedTextures(UMaterial mat)
        {
            List<UTexture2D> list = new();

            // Direct material referenced textures
            if (mat.ReferencedTextures != null)
            {
                foreach (var tex in mat.ReferencedTextures)
                    if (tex is UTexture2D t) list.Add(t);
            }

            // Expression textures
            foreach (var exprRef in mat.Expressions)
            {
                if (!exprRef.TryLoad(out var expr)) continue;

                foreach (var prop in expr.Properties)
                {
                    if (prop.Tag is ObjectProperty obj &&
                        obj.Value != null &&
                        obj.Value.TryLoad(out var texObj) &&
                        texObj is UTexture2D t)
                    {
                        list.Add(t);
                    }
                }
            }

            // CachedExpressionData → smarter extraction
            if (mat.CachedExpressionData is FStructFallback ced)
            {
                var texProp = ced.Properties?.Find(p => p.Name.Text == "ReferencedTextures");

                if (texProp?.Tag is ArrayProperty arr)
                {
                    foreach (var elem in arr.Value.Properties)
                        if (elem.GenericValue is UObject texObj && texObj is UTexture2D t)
                            list.Add(t);
                }
            }

            return list.Distinct().ToList();
        }

        private static List<UTexture2D> CollectReferencedTextures(UObject export)
        {
            var list = new HashSet<UTexture2D>();

            // 1. Works for both UMaterial + UMaterialInstance
            if (export is UMaterialInterface matInterface)
            {
                var parameters = new CMaterialParams2();

                // FULL material graph flattening (functions, parents, MI overrides)
                matInterface.GetParams(parameters, EMaterialFormat.AllLayers);

                foreach (var kv in parameters.Textures)
                {
                    if (kv.Value is UTexture2D tex)
                        list.Add(tex);
                }
            }

            // 2. MaterialInstance CachedData (UE5+)
            if (export is UMaterialInstance inst && inst.CachedData is FStructFallback cd)
            {
                foreach (var prop in cd.Properties)
                {
                    if (prop.Tag is ObjectProperty obj &&
                        obj.Value != null &&
                        obj.Value.TryLoad(out var texObj) &&
                        texObj is UTexture2D t2d)
                    {
                        list.Add(t2d);
                    }
                }
            }

            // 3. Older UMaterial fallback
            if (export is UMaterial mat && mat.ReferencedTextures != null)
            {
                foreach (var tex in mat.ReferencedTextures)
                    if (tex is UTexture2D t2d)
                        list.Add(t2d);
            }

            return list.ToList();
        }


        private static void ExportTexturePNG(UTexture2D tex, string directory)
        {
            try
            {
                CTexture ctex = tex.Decode();
                SKBitmap? bmp = ConvertToSkBitmap(ctex);
                if (bmp == null)
                {
                    Console.WriteLine("[EXPORT] Texture decode failed: " + tex.Name);
                    return;
                }

                string safe = tex.Name + ".png";
                string outputPath = Path.Combine(directory, safe);

                using var img = bmp.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(outputPath, img.ToArray());

                Console.WriteLine("[EXPORT] " + safe);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXPORT ERROR] " + ex.Message);
            }
        }

        public static void ExportReferencedTextures(string assetPath, MainWindow window, AppPakReader reader)
        {
            try
            {
                if (!reader.Provider.TryGetGameFile(assetPath, out var gameFile))
                {
                    ToastManager.ShowToast(window, $"Cannot load file: {assetPath}", ToastType.Error);
                    return;
                }

                IPackage pkg = reader.Provider.LoadPackage(gameFile);
                pkg.DeserializeAllExports();

                var export = pkg.GetExport(0);
                if (export == null)
                {
                    ToastManager.ShowToast(window, "Export is null.", ToastType.Error);
                    return;
                }

                // Pick output folder
                var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Select folder to export referenced textures"
                };

                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;

                string outDir = dlg.SelectedPath;

                // Extract
                List<UTexture2D> textures = CollectReferencedTextures(export);

                if (textures.Count == 0)
                {
                    ToastManager.ShowToast(window, "No referenced textures found.", ToastType.Info);
                    return;
                }

                foreach (var tex in textures)
                    ExportTexturePNG(tex, outDir);

                ToastManager.ShowToast(
                    window, $"Export complete.\n{textures.Count} textures exported from:\n{export.Name}", ToastType.Success, outDir);
            }
            catch (Exception ex)
            {
                ToastManager.ShowToast(window, $"Export failed:\n{ex}", ToastType.Error);
            }
        }



        public static BitmapSource BitmapToSource(Bitmap bitmap)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                ms.Position = 0;

                BitmapImage img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                return img;
            }
        }
    }
}