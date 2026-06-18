using System;
using System.IO;

namespace SimpleHttpServer
{
    // Office 文件转 PDF（使用 FreeSpire 免费版）
    public static class OfficePreview
    {
        private static readonly string _cacheDir;

        static OfficePreview()
        {
            _cacheDir = Path.Combine(Path.GetTempPath(), "SHFS_Preview");
            if (!Directory.Exists(_cacheDir))
                Directory.CreateDirectory(_cacheDir);
        }

        // 判断是否支持预览
        public static bool CanPreview(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            string e = ext.ToLowerInvariant();
            return e == ".docx" || e == ".doc"
                || e == ".xlsx" || e == ".xls"
                || e == ".pptx" || e == ".ppt";
        }

        // 转换 Office 文件为 PDF，返回 PDF 路径（带缓存）
        public static string ConvertToPdf(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            // 缓存路径：基于文件路径+修改时间
            string cacheKey = filePath.GetHashCode().ToString("x8") + "_"
                + File.GetLastWriteTimeUtc(filePath).Ticks.ToString("x");
            string pdfPath = Path.Combine(_cacheDir, cacheKey + ".pdf");

            if (File.Exists(pdfPath))
                return pdfPath;

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            bool ok = false;

            try
            {
                if (ext == ".docx" || ext == ".doc")
                    ok = ConvertDocToPdf(filePath, pdfPath);
                else if (ext == ".xlsx" || ext == ".xls")
                    ok = ConvertXlsToPdf(filePath, pdfPath);
                else if (ext == ".pptx" || ext == ".ppt")
                    ok = ConvertPptToPdf(filePath, pdfPath);
            }
            catch (Exception ex)
            {
                Logger.Error("Office 转 PDF 失败: " + ex.Message);
            }

            return ok && File.Exists(pdfPath) ? pdfPath : null;
        }

        private static bool ConvertDocToPdf(string src, string dst)
        {
            var doc = new Spire.Doc.Document();
            doc.LoadFromFile(src);
            doc.SaveToFile(dst, Spire.Doc.FileFormat.PDF);
            doc.Close();
            return true;
        }

        private static bool ConvertXlsToPdf(string src, string dst)
        {
            var wb = new Spire.Xls.Workbook();
            wb.LoadFromFile(src);
            wb.SaveToFile(dst, Spire.Xls.FileFormat.PDF);
            wb.Dispose();
            return true;
        }

        private static bool ConvertPptToPdf(string src, string dst)
        {
            var ppt = new Spire.Presentation.Presentation();
            ppt.LoadFromFile(src);
            ppt.SaveToFile(dst, Spire.Presentation.FileFormat.PDF);
            ppt.Dispose();
            return true;
        }

        // 清理过期缓存（超过 24 小时）
        public static void CleanCache()
        {
            try
            {
                if (!Directory.Exists(_cacheDir)) return;
                var cutoff = DateTime.UtcNow.AddHours(-24);
                foreach (var f in Directory.GetFiles(_cacheDir, "*.pdf"))
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff)
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }
    }
}
